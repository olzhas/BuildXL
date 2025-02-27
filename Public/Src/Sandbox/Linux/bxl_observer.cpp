// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "bxl_observer.hpp"
#include "IOHandler.hpp"
#include <stack>

static void HandleAccessReport(AccessReport report, int _)
{
    BxlObserver::GetInstance()->SendReport(report);
}

AccessCheckResult BxlObserver::sNotChecked = AccessCheckResult::Invalid();

BxlObserver* BxlObserver::GetInstance()
{
    static BxlObserver s_singleton;
    return &s_singleton;
}

BxlObserver::BxlObserver()
{
    empty_str_ = "";
    real_readlink("/proc/self/exe", progFullPath_, PATH_MAX);

    disposed_ = false;
    const char *rootPidStr = getenv(BxlEnvRootPid);
    rootPid_ = is_null_or_empty(rootPidStr) ? -1 : atoi(rootPidStr);
    // value of "1" -> special case, set by BuildXL for the root process
    if (rootPid_ == 1) {
        rootPid_ = getpid();
    }

    InitLogFile();
    InitFam();
    InitDetoursLibPath();
}

void BxlObserver::InitDetoursLibPath()
{
    const char *path = getenv(BxlEnvDetoursPath);
    if (!is_null_or_empty(path))
    {
        strlcpy(detoursLibFullPath_, path, PATH_MAX);
        detoursLibFullPath_[PATH_MAX-1] = '\0';
    }
    else
    {
        detoursLibFullPath_[0] = '\0';
    }
}

void BxlObserver::InitFam()
{
    // read FAM env var
    const char *famPath = getenv(BxlEnvFamPath);
    if (is_null_or_empty(famPath))
    {
        LOG_DEBUG("[%s] ERROR: Env var '%s' not set\n", __func__, BxlEnvFamPath);
        return;
    }

    // read FAM
    FILE *famFile = real_fopen(famPath, "rb");
    if (!famFile)
    {
        _fatal("Could not open file '%s'; errno: %d", famPath, errno);
    }

    fseek(famFile, 0, SEEK_END);
    long famLength = ftell(famFile);
    rewind(famFile);

    char *famPayload = (char *)malloc(famLength);
    real_fread(famPayload, famLength, 1, famFile);
    real_fclose(famFile);

    // create SandboxedPip (which parses FAM and throws on error)
    pip_ = shared_ptr<SandboxedPip>(new SandboxedPip(getpid(), famPayload, famLength));
    free(famPayload);

    // create sandbox
    sandbox_ = new Sandbox(0, Configuration::DetoursLinuxSandboxType);

    // initialize sandbox
    if (!sandbox_->TrackRootProcess(pip_))
    {
        _fatal("Could not track root process %s:%d", __progname, getpid());
    }

    process_ = sandbox_->FindTrackedProcess(getpid());
    process_->SetPath(progFullPath_);
    sandbox_->SetAccessReportCallback(HandleAccessReport);
}

void BxlObserver::InitLogFile()
{
    const char *logPath = getenv(BxlEnvLogPath);
    if (!is_null_or_empty(logPath))
    {
        strlcpy(logFile_, logPath, PATH_MAX);
        logFile_[PATH_MAX-1] = '\0';
    }
    else
    {
        logFile_[0] = '\0';
    }
}

bool BxlObserver::IsCacheHit(es_event_type_t event, const string &path, const string &secondPath)
{
    // (1) IMPORTANT           : never do any of this stuff after this object has been disposed!
    //     WHY                 : because the cache date structure is invalid at that point.
    //     HOW CAN THIS HAPPEN : we may get called from "on_exit" handlers, at which point the
    //                           global BxlObserver singleton instance can already be disposed.
    // (2) never cache FORK, EXEC, EXIT and events that take 2 paths
    if (disposed_ ||
        secondPath.length() > 0 ||
        event == ES_EVENT_TYPE_NOTIFY_FORK ||
        event == ES_EVENT_TYPE_NOTIFY_EXEC ||
        event == ES_EVENT_TYPE_NOTIFY_EXIT)
    {
        return false;
    }

    // coalesce some similar events
    es_event_type_t key;
    switch (event)
    {
        case ES_EVENT_TYPE_NOTIFY_TRUNCATE:
        case ES_EVENT_TYPE_NOTIFY_SETATTRLIST:
        case ES_EVENT_TYPE_NOTIFY_SETEXTATTR:
        case ES_EVENT_TYPE_NOTIFY_DELETEEXTATTR:
        case ES_EVENT_TYPE_NOTIFY_SETFLAGS:
        case ES_EVENT_TYPE_NOTIFY_SETOWNER:
        case ES_EVENT_TYPE_NOTIFY_SETMODE:
        case ES_EVENT_TYPE_NOTIFY_WRITE:
        case ES_EVENT_TYPE_NOTIFY_UTIMES:
        case ES_EVENT_TYPE_NOTIFY_SETTIME:
        case ES_EVENT_TYPE_NOTIFY_SETACL:
            key = ES_EVENT_TYPE_NOTIFY_WRITE;
            break;

        case ES_EVENT_TYPE_NOTIFY_GETATTRLIST:
        case ES_EVENT_TYPE_NOTIFY_GETEXTATTR:
        case ES_EVENT_TYPE_NOTIFY_LISTEXTATTR:
        case ES_EVENT_TYPE_NOTIFY_ACCESS:
        case ES_EVENT_TYPE_NOTIFY_STAT:
            key = ES_EVENT_TYPE_NOTIFY_STAT;

        default:
            key = event;
            break;
    }

    // This code could possibly be executing from an interrupt routine or from who knows where,
    // so to avoid deadlocks it's essential to never block here indefinitely.
    if (!cacheMtx_.try_lock_for(chrono::milliseconds(1)))
    {
        return false; // failed to acquire mutex -> forget about it
    }

    // ============================== in the critical section ================================

    // make sure the mutex is released by the end
    shared_ptr<timed_mutex> sp(&cacheMtx_, [](timed_mutex *mtx) { mtx->unlock(); });

    unordered_map<es_event_type_t, unordered_set<string>>::iterator it = cache_.find(key);
    if (it == cache_.end())
    {
        unordered_set<string> set;
        set.insert(path);
        cache_.insert(make_pair(key, set));
        return false;
    }

    return !it->second.insert(path).second;
}

bool BxlObserver::Send(const char *buf, size_t bufsiz)
{
    if (!real_open)
    {
        _fatal("syscall 'open' not found; errno: %d", errno);
    }

    // TODO: instead of failing, implement a critical section
    if (bufsiz > PIPE_BUF)
    {
        _fatal("Cannot atomically send a buffer whose size (%ld) is greater than PIPE_BUF (%d)", bufsiz, PIPE_BUF);
    }

    const char *reportsPath = GetReportsPath();
    int logFd = real_open(reportsPath, O_WRONLY | O_APPEND, 0);
    if (logFd == -1)
    {
        _fatal("Could not open file '%s'; errno: %d", reportsPath, errno);
    }

    ssize_t numWritten = real_write(logFd, buf, bufsiz);
    if (numWritten < bufsiz)
    {
        _fatal("Wrote only %ld bytes out of %ld", numWritten, bufsiz);
    }

    // A handle was opened for our own internal purposes. That
    // could have reused a fd where we missed a close, 
    // so reset that entry in the fd table
    reset_fd_table_entry(logFd);

    real_close(logFd);

    return true;
}

bool BxlObserver::SendReport(AccessReport &report)
{
    // there is no central sendbox process here (i.e., there is an instance of this
    // guy in every child process), so counting process tree size is not feasible
    if (report.operation == FileOperation::kOpProcessTreeCompleted)
    {
        return true;
    }

    const int PrefixLength = sizeof(uint);
    char buffer[PIPE_BUF] = {0};
    int maxMessageLength = PIPE_BUF - PrefixLength;
    int numWritten = snprintf(
        &buffer[PrefixLength], maxMessageLength, "%s|%d|%d|%d|%d|%d|%d|%s|%d\n",
        __progname, getpid(), report.requestedAccess, report.status, report.reportExplicitly, report.error, report.operation, report.path, report.isDirectory);
    if (numWritten == maxMessageLength)
    {
        // TODO: once 'send' is capable of sending more than PIPE_BUF at once, allocate a bigger buffer and send that
        _fatal("Message truncated to fit PIPE_BUF (%d): %s", PIPE_BUF, buffer);
    }

    LOG_DEBUG("Sending report: %s", &buffer[PrefixLength]);
    *(uint*)(buffer) = numWritten;
    return Send(buffer, numWritten + PrefixLength);
}

void BxlObserver::report_exec(const char *syscallName, const char *procName, const char *file)
{
    if (IsMonitoringChildProcesses())
    {
        // first report 'procName' as is (without trying to resolve it) to ensure that a process name is reported before anything else
        report_access(syscallName, ES_EVENT_TYPE_NOTIFY_EXEC, std::string(procName), empty_str_);
        report_access(syscallName, ES_EVENT_TYPE_NOTIFY_EXEC, file);
    }
}

AccessCheckResult BxlObserver::report_access(const char *syscallName, es_event_type_t eventType, const std::string &reportPath, const std::string &secondPath, mode_t mode)
{
    if (IsCacheHit(eventType, reportPath, secondPath))
    {
        return sNotChecked;
    }

    if (mode == 0)
    {
        // Mode hasn't been computed yet. Let's do it here.
        mode = get_mode(reportPath.c_str());
    }

    std::string execPath = eventType == ES_EVENT_TYPE_NOTIFY_EXEC
        ? reportPath
        : std::string(progFullPath_);

    IOEvent event(getpid(), 0, getppid(), eventType, ES_ACTION_TYPE_NOTIFY, reportPath, secondPath, execPath, mode, false);
    return report_access(syscallName, event, /* checkCache */ false /* because already checked cache above */);
}

AccessCheckResult BxlObserver::report_access(const char *syscallName, IOEvent &event, bool checkCache)
{
    es_event_type_t eventType = event.GetEventType();

    if (checkCache && IsCacheHit(eventType, event.GetSrcPath(), event.GetDstPath()))
    {
        return sNotChecked;
    }

    AccessCheckResult result = sNotChecked;

    if (IsEnabled())
    {
        IOHandler handler(sandbox_);
        handler.SetProcess(process_);
        result = handler.HandleEvent(event);
    }

    LOG_DEBUG("(( %10s:%2d )) %s %s%s", syscallName, event.GetEventType(), event.GetEventPath(),
        !result.ShouldReport() ? "[Ignored]" : result.ShouldDenyAccess() ? "[Denied]" : "[Allowed]",
        result.ShouldDenyAccess() && IsFailingUnexpectedAccesses() ? "[Blocked]" : "");

    return result;
}

AccessCheckResult BxlObserver::report_access(const char *syscallName, es_event_type_t eventType, const char *pathname, mode_t mode, int flags)
{
    return report_access(syscallName, eventType, normalize_path(pathname, flags), "", mode);
}

AccessCheckResult BxlObserver::report_access_fd(const char *syscallName, es_event_type_t eventType, int fd)
{
    mode_t mode = get_mode(fd);

    // If this file descriptor is a non-file (e.g., a pipe, or socket, etc.) then we don't care about it
    if (is_non_file(mode))
    {
        return sNotChecked;
    }

    std::string fullpath = fd_to_path(fd);
    
    return report_access(syscallName, eventType, fullpath, empty_str_, mode);
}

bool BxlObserver::is_non_file(const mode_t mode)
{
    // Observe we don't care about block devices here. It is unlikely that we'll support them e2e, this is just an FYI.
    return mode != 0 && !S_ISDIR(mode) && !S_ISREG(mode) && !S_ISLNK(mode);
}

AccessCheckResult BxlObserver::report_access_at(const char *syscallName, es_event_type_t eventType, int dirfd, const char *pathname, int flags)
{
    if (pathname[0] == '/')
    {
        return report_access(syscallName, eventType, pathname, /* mode */0, flags);
    }

    char fullpath[PATH_MAX] = {0};
    ssize_t len = 0;

    mode_t mode = 0;

    if (dirfd == AT_FDCWD)
    {
        if (!getcwd(fullpath, PATH_MAX))
        {
            return sNotChecked;
        }
        len = strlen(fullpath);
    }
    else
    {
        mode = get_mode(dirfd);

        // If this file descriptor is a non-file (e.g., a pipe, or socket, etc.) then we don't care about it
        if (is_non_file(mode)) 
        {
            return sNotChecked;
        }
    
        std::string dirPath = fd_to_path(dirfd);
        len = dirPath.length();
        strcpy(fullpath, dirPath.c_str());
    }

    if (len <= 0)
    {
        _fatal("Could not get path for fd %d; errno: %d", dirfd, errno);
    }

    snprintf(&fullpath[len], PATH_MAX - len, "/%s", pathname);
    return report_access(syscallName, eventType, fullpath, flags, mode);
}

AccessCheckResult BxlObserver::report_firstAllowWriteCheck(const char *fullPath)
{
    mode_t mode = get_mode(fullPath);
    bool fileExists = mode != 0 && !S_ISDIR(mode);
     
    AccessReport report =
        {
            .operation        = kOpFirstAllowWriteCheckInProcess,
            .pid              = getpid(),
            .rootPid          = pip_->GetProcessId(),
            .requestedAccess  = (int) RequestedAccess::Write,
            .status           = fileExists ? FileAccessStatus::FileAccessStatus_Denied : FileAccessStatus::FileAccessStatus_Allowed,
            .reportExplicitly = (int) ReportLevel::Report,
            .error            = 0,
            .pipId            = pip_->GetPipId(),
            .path             = {0},
            .stats            = {0},
            .isDirectory      = (uint)S_ISDIR(mode)
        };

    strlcpy(report.path, fullPath, sizeof(report.path));

    SendReport(report);

    AccessCheckResult result(RequestedAccess::Write, fileExists ? ResultAction::Deny : ResultAction::Allow, ReportLevel::Report);

    return result;
}

ssize_t BxlObserver::read_path_for_fd(int fd, char *buf, size_t bufsiz)
{
    char procPath[100] = {0};
    sprintf(procPath, "/proc/self/fd/%d", fd);
    return real_readlink(procPath, buf, bufsiz);
}

void BxlObserver::reset_fd_table_entry(int fd)
{
    if (fd >= 0 && fd < MAX_FD)
    {
        fdTable_[fd] = empty_str_;
    }
}

void BxlObserver::reset_fd_table()
{
    for (int i = 0; i < MAX_FD; i++)
    {
        fdTable_[i] = empty_str_;
    }
}

std::string BxlObserver::fd_to_path(int fd)
{
    char path[PATH_MAX] = {0};

    // ignore if fd is out of range
    if (fd < 0 || fd >= MAX_FD)
    {
        read_path_for_fd(fd, path, PATH_MAX);
        return path;
    }

    // check the file descriptor table
    if (fdTable_[fd].length() > 0)
    {
        return fdTable_[fd];
    }

    // read from the filesystem and update the file descriptor table
    read_path_for_fd(fd, path, PATH_MAX);
    fdTable_[fd] = path;
    return path;
}

std::string BxlObserver::normalize_path_at(int dirfd, const char *pathname, int oflags)
{
    // Observe that dirfd is assumed to point to a directory file descriptor. Under that assumption, it is safe to call fd_to_path for it.
    // TODO: If we wanted to be very defensive, we could also consider the case of some tool invoking any of the *at(... dirfd ...) family with a 
    // descriptor that corresponds to a non-file. This would cause the call to fail, but it might poison the file descriptor table with a non-file
    // descriptor for which we could end up not invalidating it properly.

    // no pathname given --> read path for dirfd
    if (pathname == NULL)
    {
        return fd_to_path(dirfd);
    }

    char fullpath[PATH_MAX] = {0};
    size_t len = 0;

    // if relative path --> resolve it against dirfd
    if (*pathname != '/')
    {
        if (dirfd == AT_FDCWD)
        {
            if (!getcwd(fullpath, PATH_MAX))
            {
                _fatal("Could not get CWD; errno: %d", errno);
            }
            len = strlen(fullpath);
        }
        else
        {
            std::string dirPath = fd_to_path(dirfd);
            len = dirPath.length();
            strcpy(fullpath, dirPath.c_str());
        }

        if (len <= 0)
        {
            _fatal("Could not get path for fd %d; errno: %d", dirfd, errno);
        }

        fullpath[len] = '/';
        strcpy(fullpath + len + 1, pathname);
    }
    else
    {
        strcpy(fullpath, pathname);
    }

    bool followFinalSymlink = (oflags & O_NOFOLLOW) == 0;
    resolve_path(fullpath, followFinalSymlink);

    return fullpath;
}

static void shift_left(char *str, int n)
{
    do
    {
        *(str - n) = *str;
    } while (*str++);
}

static char* find_prev_slash(char *pStr)
{
    while (*--pStr != '/');
    return pStr;
}

// resolve any intermediate directory symlinks
void BxlObserver::resolve_path(char *fullpath, bool followFinalSymlink)
{
    if (fullpath == NULL || fullpath[0] != '/')
    {
        LOG_DEBUG("Not an absolute path: %s", fullpath);
        return;
    }

    unordered_set<string> visited;

    char readlinkBuf[PATH_MAX];
    char *pFullpath = fullpath + 1;
    while (true)
    {
        // first handle "/../", "/./", and "//"
        if (*pFullpath == '/')
        {
            char *pPrevSlash = find_prev_slash(pFullpath);
            int parentDirLen = pFullpath - pPrevSlash - 1;
            if (parentDirLen == 0)
            {
                shift_left(pFullpath + 1, 1);
                continue;
            }
            else if (parentDirLen == 1 && *(pFullpath - 1) == '.')
            {
                shift_left(pFullpath + 1, 2);
                --pFullpath;
                continue;
            }
            else if (parentDirLen == 2 && *(pFullpath - 1) == '.' && *(pFullpath - 2) == '.')
            {
                // find previous slash unless already at the beginning
                if (pPrevSlash > fullpath)
                {
                    pPrevSlash = find_prev_slash(pPrevSlash);
                }
                int shiftLen = pFullpath - pPrevSlash;
                shift_left(pFullpath + 1, shiftLen);
                pFullpath = pPrevSlash + 1;
                continue;
            }
        }

        // call readlink for intermediate dirs and the final path if followSymlink is true
        ssize_t nReadlinkBuf = -1;
        char ch = *pFullpath;
        if (*pFullpath == '/' || (*pFullpath == '\0' && followFinalSymlink))
        {
            *pFullpath = '\0';
            nReadlinkBuf = real_readlink(fullpath, readlinkBuf, PATH_MAX);
            *pFullpath = ch;
        }

        // if not a symlink --> either continue or exit if at the end of the path
        if (nReadlinkBuf == -1)
        {
            if (*pFullpath == '\0')
            {
                break;
            }
            else
            {
                ++pFullpath;
                continue;
            }
        }

        // current path is a symlink
        readlinkBuf[nReadlinkBuf] = '\0';

        // report readlink for the current path
        *pFullpath = '\0';
        // break if the same symlink has already been visited (breaks symlink loops)
        if (!visited.insert(fullpath).second) break;
        report_access("_readlink", ES_EVENT_TYPE_NOTIFY_READLINK, std::string(fullpath), empty_str_);
        *pFullpath = ch;

        // append the rest of the original path to the readlink target
        strcpy(
            readlinkBuf + nReadlinkBuf,
            (readlinkBuf[nReadlinkBuf-1] == '/' && *pFullpath == '/') ? pFullpath + 1 : pFullpath);

        // if readlink target is an absolute path -> overwrite fullpath with it and start from the beginning
        if (readlinkBuf[0] == '/')
        {
            strcpy(fullpath, readlinkBuf);
            pFullpath = fullpath + 1;
            continue;
        }

        // readlink target is a relative path -> replace the current dir in fullpath with the target
        pFullpath = find_prev_slash(pFullpath);
        strcpy(++pFullpath, readlinkBuf);
    }
}

char** BxlObserver::ensure_env_value_with_log(char *const envp[], char const *envName)
{
    char *envValue = getenv(envName);
    if (is_null_or_empty(envValue))
    {
        return (char**)envp;
    }

    char **newEnvp = ensure_env_value(envp, envName, envValue);
    if (newEnvp != envp)
    {
        LOG_DEBUG("envp has been modified with %s added to %s", envValue, envName);
    }

    return newEnvp;
}

char** BxlObserver::ensureEnvs(char *const envp[])
{
    if (!IsMonitoringChildProcesses())
    {
        char **newEnvp = remove_path_from_LDPRELOAD(envp, detoursLibFullPath_);
        newEnvp = ensure_env_value(newEnvp, BxlEnvFamPath, "");
        newEnvp = ensure_env_value(newEnvp, BxlEnvLogPath, "");
        newEnvp = ensure_env_value(newEnvp, BxlEnvDetoursPath, "");
        newEnvp = ensure_env_value(newEnvp, BxlEnvRootPid, "");
        return newEnvp;
    }
    else
    {
        char **newEnvp = ensure_paths_included_in_env(envp, LD_PRELOAD_ENV_VAR_PREFIX, detoursLibFullPath_, NULL);
        if (newEnvp != envp)
        {
            LOG_DEBUG("envp has been modified with %s added to %s", detoursLibFullPath_, "LD_PRELOAD");
        }

        newEnvp = ensure_env_value_with_log(newEnvp, BxlEnvFamPath);
        newEnvp = ensure_env_value_with_log(newEnvp, BxlEnvLogPath);
        newEnvp = ensure_env_value_with_log(newEnvp, BxlEnvDetoursPath);
        newEnvp = ensure_env_value(newEnvp, BxlEnvRootPid, "");

        return newEnvp;
    }
}

bool BxlObserver::EnumerateDirectory(std::string rootDirectory, bool recursive, std::vector<std::string>& filesAndDirectories)
{
    std::stack<std::string> directoriesToEnumerate;
    DIR *dir;
    struct dirent *ent;

    filesAndDirectories.clear();
    directoriesToEnumerate.push(rootDirectory);
    filesAndDirectories.push_back(rootDirectory);

    while (!directoriesToEnumerate.empty())
    {
        auto currentDirectory = directoriesToEnumerate.top();
        directoriesToEnumerate.pop();

        dir = real_opendir(currentDirectory.c_str());

        if (dir != NULL)
        {
            while ((ent = readdir(dir)) != NULL)
            {
                std::string fileOrDirectory(ent->d_name);
                if (fileOrDirectory == "." || fileOrDirectory == "..")
                {
                    continue;
                }

                std::string fullPath = currentDirectory + "/" + fileOrDirectory;

                // NOTE: d_type is supported on these filesystems as of 2022 which should cover all BuildXL cases: Btrfs, ext2, ext3, and ext4
                if (ent->d_type == DT_DIR && recursive)
                {
                    // DT_DIR = Directory
                    directoriesToEnumerate.push(fullPath);
                }

                filesAndDirectories.push_back(fullPath);
            }

            closedir(dir);
        }
        else
        {
            // Something went wrong with opendir
            LOG_DEBUG("[BxlObserver::EnumerateDirectory] opendir failed on '%s' with errno %d\n", currentDirectory, errno);
            return false;
        }
    }

    return true;
}