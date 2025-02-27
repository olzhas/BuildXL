// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// disable 'Missing XML comment for publicly visible type' warnings.
#pragma warning disable 1591
#pragma warning disable SA1600 // Elements must be documented

namespace BuildXL.Cache.ContentStore.Hashing
{
    public static class VsoHash
    {
        public const byte VsoAlgorithmId = 0;
        private const int PagesPerBlock = 32;
        public const int PageSize = 64 * 1024;
        public const int BlockSize = PagesPerBlock * PageSize; // 32 * 64 * 1024 = 2MB

        public const int HashSize = 32;

        public static readonly BlobIdentifierWithBlocks OfNothing;

        private static readonly ArrayPool<byte> ArrayPool = ArrayPool<byte>.Shared;
        private static readonly Pool<List<byte>> PoolLocalPageIdsBuffer = new Pool<List<byte>>(factory: () => new List<byte>(PagesPerBlock), reset: list => list.Clear());
#pragma warning disable SYSLIB0021 // Type or member is obsolete. Temporarily suppressing the warning for .net 6. Work item: 1885580
        private static readonly Pool<SHA256CryptoServiceProvider> PoolSHA256CryptoServiceProvider = new Pool<SHA256CryptoServiceProvider>(() => new SHA256CryptoServiceProvider());
#pragma warning restore SYSLIB0021 // Type or member is obsolete

        static VsoHash()
        {
            using (var emptyStream = new MemoryStream())
            {
                OfNothing = CalculateBlobIdentifierWithBlocks(emptyStream);
            }
        }

        private delegate void MultipleBlockBlobCallback(byte[] block, int blockLength, BlobBlockHash blockHash, bool isFinalBlock);

        public delegate Task MultipleBlockBlobCallbackAsync(byte[] block, int blockLength, BlobBlockHash blockHash, bool isFinalBlock);

        private delegate void MultipleBlockBlobSealCallback(BlobIdentifierWithBlocks? blobIdWithBlocks);

        public delegate Task MultipleBlockBlobSealCallbackAsync(BlobIdentifierWithBlocks? blobIdWithBlocks);

        private delegate void SingleBlockBlobCallback(byte[] block, int blockLength, BlobIdentifierWithBlocks blobIdWithBlocks);

        public delegate Task SingleBlockBlobCallbackAsync(byte[] block, int blockLength, BlobIdentifierWithBlocks blobIdWithBlocks);

        private delegate Task BlockReadCompleteAsync(Pool<byte[]>.PoolHandle blockBufferHandle, int blockLength, BlobBlockHash blockHash);

        private delegate void BlockReadComplete(Pool<byte[]>.PoolHandle blockBufferHandle, int blockLength, BlobBlockHash blockHash);

#if NETCOREAPP
        public static BlobBlockHash HashBlock(byte[] block, int lengthToHash, int startIndex = 0)
        {
            var buffer = new byte[HashSize];
            HashBlockBytes(block, lengthToHash, buffer, startIndex);
            return new BlobBlockHash(buffer);
        }

        /// <summary>
        /// Expose method which does not allocate a BlobBlockHash
        /// </summary>
        public static void HashBlockBytes(byte[] block, int lengthToHash, byte[] resultBuffer, int startIndex = 0)
        {
            HashBlockBytes(block.AsSpan(startIndex, lengthToHash), resultBuffer);
        }

        /// <summary>
        /// Expose method which does not allocate a BlobBlockHash
        /// </summary>
        public static void HashBlockBytes(ReadOnlySpan<byte> block, byte[] resultBuffer)
        {
            var lengthToHash = block.Length;
            using (var sha256Handle = PoolSHA256CryptoServiceProvider.Get())
            {
                int pageCounter = 0;
                int currentIndex = 0;
                int endIndex = lengthToHash;

                int expectedPages = (int)Math.Ceiling(lengthToHash * 1.0 / PageSize);
                int bytesPerPage = sha256Handle.Value.HashSize / 8;
                var totalBytes = expectedPages * bytesPerPage;

                Contract.AssertDebug(bytesPerPage == HashSize, $"HashSize constant is not consistent with crypto provider value. HashSize=[{HashSize}], Provider=[{bytesPerPage}]");
                Contract.AssertDebug(resultBuffer.Length == HashSize, $"The resultBuffer has length {resultBuffer.Length} but it should have length {HashSize}");
                Contract.AssertDebug(expectedPages <= PagesPerBlock, $"Block will have {expectedPages}, which is greater than the maximum of {PagesPerBlock}");

                var buffer = ArrayPool.Rent(totalBytes);
                var bufferSpan = buffer.AsSpan().Slice(0, totalBytes);
                try
                {
                    while (endIndex > currentIndex)
                    {
                        int bytesToCopy = Math.Min(endIndex - currentIndex, PageSize);
                        var page = block.Slice(currentIndex, bytesToCopy);
                        var pageBuffer = bufferSpan.Slice(pageCounter * bytesPerPage);
                        var succeeded = sha256Handle.Value.TryComputeHash(page, pageBuffer, out var bytesWritten);
                        if (!succeeded)
                        {
                            // From the docs: TryComputeHash will return true if destination is long enough to receive the hash value; otherwise, false.
                            throw new InvalidOperationException("Page buffer is not big enough to receive the hash value.");
                        }

                        Contract.AssertDebug(bytesWritten == bytesPerPage, $"Unexpected amount of bytes written.Expected =[{bytesPerPage}] Actual =[{bytesWritten}]");

                        pageCounter++;
                        currentIndex += PageSize;
                    }

                    // calculate the block buffer as we have make pages or have a partial page
                    var resultSucceeded = TryComputeSHA256Hash(bufferSpan, resultBuffer);
                    if (!resultSucceeded)
                    {
                        // From the docs: TryComputeHash will return true if destination is long enough to receive the hash value; otherwise, false.
                        throw new InvalidOperationException("Result buffer is not big enough to receive the hash value.");
                    }
                }
                finally
                {
                    ArrayPool.Return(buffer);
                }
            }
        }

        private static bool TryComputeSHA256Hash(ReadOnlySpan<byte> bytes, Span<byte> result)
        {
            using (var sha256Handle = PoolSHA256CryptoServiceProvider.Get())
            {
                var succeeded = sha256Handle.Value.TryComputeHash(bytes, result, out var bytesWritten);
#if DEBUG
                if (succeeded && bytesWritten != HashSize)
                {
                    throw new Exception($"Expected crypto service provider to write {HashSize} bytes, but it wrote {bytesWritten} bytes instead.");
                }
#endif
                return succeeded;
            }
        }
#else // !NETCOREAPP
        public static BlobBlockHash HashBlock(byte[] block, int lengthToHash, int startIndex = 0)
        {
            using (var pageIdsHandle = PoolLocalPageIdsBuffer.Get())
            using (var sha256Handle = PoolSHA256CryptoServiceProvider.Get())
            {
                List<byte> pageIdentifiersBuffer = pageIdsHandle.Value;
                int pageCounter = 0;
                int currentIndex = startIndex;
                int endIndex = startIndex + lengthToHash;
                while (endIndex > currentIndex)
                {
                    int bytesToCopy = Math.Min(endIndex - currentIndex, PageSize);
                    byte[] pageHash = sha256Handle.Value.ComputeHash(block, currentIndex, bytesToCopy);
                    pageCounter++;
                    currentIndex += PageSize;
                    pageIdentifiersBuffer.AddRange(pageHash);
                    if (pageCounter > PagesPerBlock)
                    {
                        throw new InvalidOperationException("Block has too many pages");
                    }
                }

                // calculate the block buffer as we have make pages or have a partial page
                return new BlobBlockHash(ComputeSHA256Hash(pageIdentifiersBuffer.ToArray()));
            }
        }
#endif

        public static async Task<BlobIdentifierWithBlocks> WalkAllBlobBlocksAsync(
            Stream stream,
            SemaphoreSlim blockActionSemaphore,
            bool multipleBlocksInParallel,
            MultipleBlockBlobCallbackAsync multipleBlockCallback,
            long? bytesToReadFromStream = null)
        {
            bytesToReadFromStream ??= (stream.Length - stream.Position);
            BlobIdentifierWithBlocks? blobIdWithBlocks = null;
            await WalkMultiBlockBlobAsync(
                stream,
                blockActionSemaphore,
                multipleBlocksInParallel,
                multipleBlockCallback,
                computedBlobIdWithBlocks =>
                {
                    blobIdWithBlocks = computedBlobIdWithBlocks;
                    return Task.FromResult(0);
                },
                bytesToReadFromStream.GetValueOrDefault()).ConfigureAwait(false);
            return blobIdWithBlocks!;
        }

        /// <summary>
        /// Asynchronously walks a stream, calling back into supplied delegates at a block level
        /// </summary>
        /// <param name="stream">The stream to read bytes from.  The caller is responsible for correctly setting the stream's starting positon.</param>
        /// <param name="blockActionSemaphore">Optional: If non-null, a SemaphoreSlim to bound the number of callbacks in flight.  This can be used to bound the number of block-sized that are allocated at any one time.</param>
        /// <param name="multipleBlocksInParallel">Only affects multi-block blobs.  Determines if multiBlockCallback delegates are called in parallel (True) or serial (False).</param>
        /// <param name="singleBlockCallback">Only will be called if the blob is composed of a single block. Is called with the byte buffer for the block, the length of block (possibly less than buffer's length), and the hash of the block.</param>
        /// <param name="multipleBlockCallback">Only will be called if the blob is composed of a multiple blocks. Is called with the byte buffer for the block, the length of block (possibly less than buffer's length), the index of this block, the hash of the block, and whether or not this is the final block.</param>
        /// <param name="multipleBlockSealCallback">Only will be called if the blob is composed of a multiple blocks. Is called after all multiBlockCallback delegates have returned.</param>
        /// <param name="bytesToReadFromStream">Number of bytes to read from the stream. Specify -1 to read to the end of the stream.</param>
        public static async Task WalkBlocksAsync(
            Stream stream,
            SemaphoreSlim? blockActionSemaphore,
            bool multipleBlocksInParallel,
            SingleBlockBlobCallbackAsync singleBlockCallback,
            MultipleBlockBlobCallbackAsync multipleBlockCallback,
            MultipleBlockBlobSealCallbackAsync multipleBlockSealCallback,
            long bytesToReadFromStream = -1)
        {
            bytesToReadFromStream = (bytesToReadFromStream >= 0) ? bytesToReadFromStream : (stream.Length - stream.Position);
            bool isSingleBlockBlob = bytesToReadFromStream <= BlockSize;

            if (isSingleBlockBlob)
            {
                await WalkSingleBlockBlobAsync(stream, blockActionSemaphore, singleBlockCallback, bytesToReadFromStream).ConfigureAwait(false);
            }
            else
            {
                await WalkMultiBlockBlobAsync(stream, blockActionSemaphore, multipleBlocksInParallel, multipleBlockCallback, multipleBlockSealCallback, bytesToReadFromStream).ConfigureAwait(false);
            }
        }

        private static void WalkBlocks(
            Stream stream,
            SemaphoreSlim? blockActionSemaphore,
            bool multipleBlocksInParallel,
            SingleBlockBlobCallback singleBlockCallback,
            MultipleBlockBlobCallback multipleBlockCallback,
            MultipleBlockBlobSealCallback multipleBlockSealCallback,
            long bytesToReadFromStream = -1)
        {
            bytesToReadFromStream = (bytesToReadFromStream >= 0) ? bytesToReadFromStream : (stream.Length - stream.Position);
            bool isSingleBlockBlob = bytesToReadFromStream <= BlockSize;

            if (isSingleBlockBlob)
            {
                WalkSingleBlockBlob(stream, blockActionSemaphore, singleBlockCallback, bytesToReadFromStream);
            }
            else
            {
                WalkMultiBlockBlob(stream, blockActionSemaphore, multipleBlocksInParallel, multipleBlockCallback, multipleBlockSealCallback, bytesToReadFromStream);
            }
        }

        public static BlobIdentifierWithBlocks CalculateBlobIdentifierWithBlocks(Stream stream)
        {
            BlobIdentifierWithBlocks? result = null;

            WalkBlocks(
                stream,
                blockActionSemaphore: null,
                multipleBlocksInParallel: false,
                singleBlockCallback: (block, blockLength, blobIdWithBlocks) =>
                {
                    result = blobIdWithBlocks;
                },
                multipleBlockCallback: (block, blockLength, blockHash, isFinalBlock) =>
                {
                },
                multipleBlockSealCallback: blobIdWithBlocks =>
                {
                    result = blobIdWithBlocks;
                });

            if (result == null)
            {
                throw new InvalidOperationException("Program error: did not calculate a value.");
            }

            return result;
        }

        public static async Task<BlobIdentifierWithBlocks> CalculateBlobIdentifierWithBlocksAsync(Stream stream)
        {
            BlobIdentifierWithBlocks? result = null;

            await WalkBlocksAsync(
                stream,
                blockActionSemaphore: null,
                multipleBlocksInParallel: false,
                singleBlockCallback: (block, blockLength, blobIdWithBlocks) =>
                {
                    result = blobIdWithBlocks;
                    return Task.FromResult(0);
                },
                multipleBlockCallback: (block, blockLength, blockHash, isFinalBlock) => Task.FromResult(0),
                multipleBlockSealCallback: blobIdWithBlocks =>
                {
                    result = blobIdWithBlocks;
                    return Task.FromResult(0);
                }).ConfigureAwait(false);

            if (result == null)
            {
                throw new InvalidOperationException($"Program error: {nameof(CalculateBlobIdentifierWithBlocksAsync)} did not calculate a value.");
            }

            return result;
        }

        public static BlobIdentifier CalculateBlobIdentifier(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            return CalculateBlobIdentifierWithBlocks(stream).BlobId;
        }

        public static BlobIdentifier CalculateBlobIdentifier(byte[] content)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            using (var stream = new MemoryStream(content))
            {
                return CalculateBlobIdentifierWithBlocks(stream).BlobId;
            }
        }
        
        internal static byte[] ComputeSHA256Hash(byte[] byteArray)
        {
            using (var sha256Handle = PoolSHA256CryptoServiceProvider.Get())
            {
                return sha256Handle.Value.ComputeHash(byteArray, 0, byteArray.Length);
            }
        }

        private static void ReadBlock(Stream stream, SemaphoreSlim? blockActionSemaphore, long bytesLeftInBlob, BlockReadComplete readCallback)
        {
            blockActionSemaphore?.Wait();

            bool disposeNeeded = true;
            try
            {
                Pool<byte[]>.PoolHandle blockBufferHandle = GlobalObjectPools.TwoMbByteArrayPool.Get();
                try
                {
                    byte[] blockBuffer = blockBufferHandle.Value;
                    int bytesToRead = (int)Math.Min(BlockSize, bytesLeftInBlob);
                    int bufferOffset = 0;
                    while (bytesToRead > 0)
                    {
                        int bytesRead = stream.Read(blockBuffer, bufferOffset, bytesToRead);
                        bytesToRead -= bytesRead;
                        bufferOffset += bytesRead;
                        if (bytesRead == 0)
                        {
                            // ReadAsync returns 0 when the stream has ended.
                            if (bytesToRead > 0)
                            {
                                throw new EndOfStreamException();
                            }
                        }
                    }

                    BlobBlockHash blockHash = HashBlock(blockBuffer, bufferOffset);
                    disposeNeeded = false;
                    readCallback(blockBufferHandle, bufferOffset, blockHash);
                }
                finally
                {
                    if (disposeNeeded)
                    {
                        blockBufferHandle.Dispose();
                    }
                }
            }
            finally
            {
                if (disposeNeeded)
                {
                    blockActionSemaphore?.Release();
                }
            }
        }

        private static async Task ReadBlockAsync(
            Stream stream,
            SemaphoreSlim? blockActionSemaphore,
            long bytesLeftInBlob,
            BlockReadCompleteAsync readCallback)
        {
            if (blockActionSemaphore != null)
            {
                await blockActionSemaphore.WaitAsync().ConfigureAwait(false);
            }

            bool disposeNeeded = true;
            try
            {
                Pool<byte[]>.PoolHandle blockBufferHandle = GlobalObjectPools.TwoMbByteArrayPool.Get();
                try
                {
                    byte[] blockBuffer = blockBufferHandle.Value;
                    int bytesToRead = (int)Math.Min(BlockSize, bytesLeftInBlob);
                    int bufferOffset = 0;
                    while (bytesToRead > 0)
                    {
                        int bytesRead = await stream.ReadAsync(blockBuffer, bufferOffset, bytesToRead).ConfigureAwait(false);
                        bytesToRead -= bytesRead;
                        bufferOffset += bytesRead;
                        if (bytesRead == 0)
                        {
                            // ReadAsync returns 0 when the stream has ended.
                            if (bytesToRead > 0)
                            {
                                throw new EndOfStreamException();
                            }
                        }
                    }

                    BlobBlockHash blockHash = HashBlock(blockBuffer, bufferOffset);
                    disposeNeeded = false; // readCallback is now responsible for disposing the blockBufferHandle
                    await readCallback(blockBufferHandle, bufferOffset, blockHash).ConfigureAwait(false);
                }
                finally
                {
                    if (disposeNeeded)
                    {
                        blockBufferHandle.Dispose();
                    }
                }
            }
            finally
            {
                if (disposeNeeded)
                {
                    blockActionSemaphore?.Release();
                }
            }
        }

        private static void WalkMultiBlockBlob(
            Stream stream,
            SemaphoreSlim? blockActionSemaphore,
            bool multiBlocksInParallel,
            MultipleBlockBlobCallback multipleBlockCallback,
            MultipleBlockBlobSealCallback multipleBlockSealCallback,
            long bytesLeftInBlob)
        {
            var rollingId = new RollingBlobIdentifierWithBlocks();
            BlobIdentifierWithBlocks? blobIdentifierWithBlocks = null;

            Lazy<List<Task>> tasks = new Lazy<List<Task>>(() => new List<Task>());
            do
            {
                ReadBlock(
                    stream,
                    blockActionSemaphore,
                    bytesLeftInBlob,
                    (blockBufferHandle, blockLength, blockHash) =>
                    {
                        bytesLeftInBlob -= blockLength;
                        bool isFinalBlock = bytesLeftInBlob == 0;

                        try
                        {
                            if (isFinalBlock)
                            {
                                blobIdentifierWithBlocks = rollingId.Finalize(blockHash);
                            }
                            else
                            {
                                rollingId.Update(blockHash);
                            }
                        }
                        catch
                        {
                            CleanupBufferAndSemaphore(blockBufferHandle, blockActionSemaphore);
                            throw;
                        }

                        if (multiBlocksInParallel)
                        {
                            tasks.Value.Add(Task.Run(() =>
                            {
                                try
                                {
                                    multipleBlockCallback(blockBufferHandle.Value, blockLength, blockHash, isFinalBlock);
                                }
                                finally
                                {
                                    CleanupBufferAndSemaphore(blockBufferHandle, blockActionSemaphore);
                                }
                            }));
                        }
                        else
                        {
                            try
                            {
                                multipleBlockCallback(blockBufferHandle.Value, blockLength, blockHash, isFinalBlock);
                            }
                            finally
                            {
                                CleanupBufferAndSemaphore(blockBufferHandle, blockActionSemaphore);
                            }
                        }
                    });
            }
            while (bytesLeftInBlob > 0);

            if (tasks.IsValueCreated)
            {
                Task.WaitAll(tasks.Value.ToArray());
            }

            multipleBlockSealCallback(blobIdentifierWithBlocks);
        }

        private static async Task WalkMultiBlockBlobAsync(
            Stream stream,
            SemaphoreSlim? blockActionSemaphore,
            bool multiBlocksInParallel,
            MultipleBlockBlobCallbackAsync multipleBlockCallback,
            MultipleBlockBlobSealCallbackAsync multipleBlockSealCallback,
            long bytesLeftInBlob)
        {
            var rollingId = new RollingBlobIdentifierWithBlocks();
            BlobIdentifierWithBlocks? blobIdentifierWithBlocks = null;

            var tasks = new List<Task>();
            do
            {
                await ReadBlockAsync(
                    stream,
                    blockActionSemaphore,
                    bytesLeftInBlob,
                    async (blockBufferHandle, blockLength, blockHash) =>
                    {
                        bytesLeftInBlob -= blockLength;
                        bool isFinalBlock = bytesLeftInBlob == 0;

                        try
                        {
                            if (isFinalBlock)
                            {
                                blobIdentifierWithBlocks = rollingId.Finalize(blockHash);
                            }
                            else
                            {
                                rollingId.Update(blockHash);
                            }
                        }
                        catch
                        {
                            CleanupBufferAndSemaphore(blockBufferHandle, blockActionSemaphore);
                            throw;
                        }

                        Task multiBlockTask = Task.Run(async () =>
                        {
                            try
                            {
                                await multipleBlockCallback(blockBufferHandle.Value, blockLength, blockHash, isFinalBlock).ConfigureAwait(false);
                            }
                            finally
                            {
                                CleanupBufferAndSemaphore(blockBufferHandle, blockActionSemaphore);
                            }
                        });
                        tasks.Add(multiBlockTask);

                        if (!multiBlocksInParallel)
                        {
                            await multiBlockTask.ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
            }
            while (bytesLeftInBlob > 0);

            await Task.WhenAll(tasks).ConfigureAwait(false);
            await multipleBlockSealCallback(blobIdentifierWithBlocks).ConfigureAwait(false);
        }

        private static void CleanupBufferAndSemaphore(
            Pool<byte[]>.PoolHandle blockBufferHandle,
            SemaphoreSlim? blockActionSemaphore)
        {
            blockBufferHandle.Dispose();
            blockActionSemaphore?.Release();
        }

        private static void WalkSingleBlockBlob(Stream stream, SemaphoreSlim? blockActionSemaphore, SingleBlockBlobCallback singleBlockCallback, long bytesLeftInBlob)
        {
            ReadBlock(
                stream,
                blockActionSemaphore,
                bytesLeftInBlob,
                (blockBufferHandle, blockLength, blockHash) =>
                {
                    try
                    {
                        var rollingId = new RollingBlobIdentifierWithBlocks();
                        var blobIdentifierWithBlocks = rollingId.Finalize(blockHash);
                        singleBlockCallback(blockBufferHandle.Value, blockLength, blobIdentifierWithBlocks);
                    }
                    finally
                    {
                        blockBufferHandle.Dispose();
                        blockActionSemaphore?.Release();
                    }
                });
        }

        private static Task WalkSingleBlockBlobAsync(
            Stream stream,
            SemaphoreSlim? blockActionSemaphore,
            SingleBlockBlobCallbackAsync singleBlockCallback,
            long bytesLeftInBlob)
        {
            return ReadBlockAsync(
                stream,
                blockActionSemaphore,
                bytesLeftInBlob,
                async (blockBufferHandle, blockLength, blockHash) =>
                {
                    try
                    {
                        var rollingId = new RollingBlobIdentifierWithBlocks();
                        var blobIdentifierWithBlocks = rollingId.Finalize(blockHash);
                        await singleBlockCallback(blockBufferHandle.Value, blockLength, blobIdentifierWithBlocks).ConfigureAwait(false);
                    }
                    finally
                    {
                        blockBufferHandle.Dispose();
                        blockActionSemaphore?.Release();
                    }
                });
        }

        public class RollingBlobIdentifierWithBlocks
        {
            private readonly RollingBlobIdentifier _inner;
            private readonly List<BlobBlockHash> _blockHashes;

            public RollingBlobIdentifierWithBlocks()
            {
                _inner = new RollingBlobIdentifier();
                _blockHashes = new List<BlobBlockHash>();
            }

            public void Update(BlobBlockHash currentBlockIdentifier)
            {
                _blockHashes.Add(currentBlockIdentifier);
                _inner.Update(currentBlockIdentifier);
            }

            public BlobIdentifierWithBlocks Finalize(BlobBlockHash currentBlockIdentifier)
            {
                _blockHashes.Add(currentBlockIdentifier);
                var blobId = _inner.Finalize(currentBlockIdentifier);
                return new BlobIdentifierWithBlocks(blobId, _blockHashes);
            }
        }

        public class RollingBlobIdentifier
        {
            private static readonly byte[] InitialRollingId = Encoding.ASCII.GetBytes("VSO Content Identifier Seed");
            private byte[] _rollingId = InitialRollingId;
            private bool _finalAdded;

            private static readonly ArrayPool<byte> ArrayPool = ArrayPool<byte>.Shared;

#if NETCOREAPP
            public void Update(byte[] currentBlockIdentifier)
            {
                // TODO:if we want to enforce this we should implement BlobBlockHash.BlockSize (bug 1365340)
                //
                // var currentBlockSize = currentBlockIdentifier.BlockSize;
                // if (currentBlockSize != BlockSize)
                // {
                //     throw new InvalidOperationException($"Non-final blocks must be of size {BlockSize}; but the given block has size {currentBlockSize}");
                // }
                UpdateInternal(currentBlockIdentifier, false);
            }

            public BlobIdentifier Finalize(byte[] currentBlockIdentifier)
            {
                // TODO:if we want to enforce this we should implement BlobBlockHash.BlockSize (bug 1365340)
                //
                // if (blockSize > BlockSize)
                // {
                //     throw new InvalidOperationException("Blocks cannot be bigger than BlockSize.");
                // }
                UpdateInternal(currentBlockIdentifier, true);
                return new BlobIdentifier(_rollingId, VsoAlgorithmId);
            }

            private void UpdateInternal(byte[] currentBlockIdentifier, bool isFinalBlock)
            {
                if (_finalAdded && isFinalBlock)
                {
                    throw new InvalidOperationException("Final block already added.");
                }

                int combinedBufferLength = _rollingId.Length + currentBlockIdentifier.Length + 1;
                var combinedBuffer = ArrayPool.Rent(combinedBufferLength);
                try
                {
                    Array.Copy(_rollingId, combinedBuffer, _rollingId.Length);
                    Array.Copy(currentBlockIdentifier, 0, combinedBuffer, _rollingId.Length, currentBlockIdentifier.Length);
                    combinedBuffer[combinedBufferLength - 1] = Convert.ToByte(isFinalBlock);

                    if (_rollingId == InitialRollingId)
                    {
                        // Initial ID has a different size from the rest. Need to allocate new array.

                        // It's possible that the rented array is not the exact size needed.
                        var bufferToHash = combinedBuffer;
                        if (combinedBuffer.Length != combinedBufferLength)
                        {
                            bufferToHash = new byte[combinedBufferLength];
                            Array.Copy(combinedBuffer, 0, bufferToHash, 0, combinedBufferLength);
                        }

                        _rollingId = ComputeSHA256Hash(bufferToHash);
                    }
                    else
                    {
                        // Can reuse same buffer.
                        var succeeded = TryComputeSHA256Hash(combinedBuffer.AsSpan().Slice(0, combinedBufferLength), _rollingId);
                        if (!succeeded)
                        {
                            throw new InvalidOperationException($"rollingId.Length=[{_rollingId.Length}] is not large enough to receive hash value.");
                        }
                    }
                }
                finally
                {
                    ArrayPool.Return(combinedBuffer);
                }

                if (isFinalBlock)
                {
                    _finalAdded = true;
                }
            }
#else
            private void UpdateInternal(BlobBlockHash currentBlockIdentifier, bool isFinalBlock)
            {
                if (_finalAdded && isFinalBlock)
                {
                    throw new InvalidOperationException("Final block already added.");
                }

                int combinedBufferLength = _rollingId.Length + currentBlockIdentifier.HashBytes.Length + 1;
                var combinedBuffer = new byte[combinedBufferLength];
                Array.Copy(_rollingId, combinedBuffer, _rollingId.Length);
                Array.Copy(currentBlockIdentifier.HashBytes, 0, combinedBuffer, _rollingId.Length, currentBlockIdentifier.HashBytes.Length);
                combinedBuffer[combinedBufferLength - 1] = Convert.ToByte(isFinalBlock);
                _rollingId = ComputeSHA256Hash(combinedBuffer);

                if (isFinalBlock)
                {
                    _finalAdded = true;
                }
            }
#endif

            public void Update(BlobBlockHash currentBlockIdentifier)
            {
                // TODO:if we want to enforce this we should implement BlobBlockHash.BlockSize (bug 1365340)
                //
                // var currentBlockSize = currentBlockIdentifier.BlockSize;
                // if (currentBlockSize != BlockSize)
                // {
                //     throw new InvalidOperationException($"Non-final blocks must be of size {BlockSize}; but the given block has size {currentBlockSize}");
                // }

#if NETCOREAPP
                UpdateInternal(currentBlockIdentifier.HashBytes, false);
#else
                UpdateInternal(currentBlockIdentifier, false);
#endif
            }

            public BlobIdentifier Finalize(BlobBlockHash currentBlockIdentifier)
            {
                // TODO:if we want to enforce this we should implement BlobBlockHash.BlockSize (bug 1365340)
                //
                // if (blockSize > BlockSize)
                // {
                //     throw new InvalidOperationException("Blocks cannot be bigger than BlockSize.");
                // }

#if NETCOREAPP
                UpdateInternal(currentBlockIdentifier.HashBytes, true);
#else
                UpdateInternal(currentBlockIdentifier, true);
#endif
                return new BlobIdentifier(_rollingId, VsoAlgorithmId);
            }
        }
    }
}
