// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using ContentStore.Grpc;
using Google.Protobuf;
using Grpc.Core;
using BuildXL.Utilities;
using BuildXL.Utilities.Tracing;
using BuildXL.Cache.ContentStore.Distributed;

#nullable enable

namespace BuildXL.Cache.ContentStore.Service.Grpc
{
    /// <summary>
    /// Extension methods for GRPC code.
    /// </summary>
    public static class GrpcExtensions
    {
        /// <summary>
        /// Converts a bytestring to a contenthash
        /// </summary>
        public static ContentHash ToContentHash(this ByteString byteString, HashType hashType)
        {
            return new ContentHash(hashType, byteString.Span);
        }

        /// <summary>
        /// Converts a <see cref="CopyFileRequest"/> to a <see cref="ContentHash"/>.
        /// </summary>
        public static ContentHash GetContentHash(this CopyFileRequest request) => request.ContentHash.ToContentHash((HashType)request.HashType);

        /// <summary>
        /// Converts a bytestring to a contenthash
        /// </summary>
        public static ByteString ToByteString(in this ContentHash contentHash)
        {
            byte[] hashByteArray = contentHash.ToHashByteArray();
            return ByteString.CopyFrom(hashByteArray, 0, hashByteArray.Length);
        }

        /// <nodoc />
        public static async Task<(long totalChunkCount, long totalBytes)> CopyChunksToStreamAsync<T>(
            IAsyncStreamReader<T> input,
            Stream output,
            Func<T, ByteString> transform,
            Action<CopyStatistics>? progressReport = null,
            CancellationToken cancellationToken = default)
        {
            long totalChunksRead = 0L;
            long totalBytesRead = 0L;
            AsyncOut<TimeSpan> copyDuration = new AsyncOut<TimeSpan>();

            Task<bool> hasCurrentElement = MeasureCopyAsync(
                    async () => await input.MoveNext(cancellationToken),
                    copyDuration);

            while (await hasCurrentElement)
            {
                totalChunksRead++;
                ByteString chunk = transform(input.Current);
                totalBytesRead += chunk.Length;

                var fetchNextTask = MeasureCopyAsync(
                    async () => await input.MoveNext(cancellationToken),
                    copyDuration);

                progressReport?.Invoke(new CopyStatistics(totalBytesRead, copyDuration.Value));
                await Task.WhenAll(fetchNextTask, output.WriteByteStringAsync(chunk, cancellationToken));
                hasCurrentElement = fetchNextTask;
            }

            return (totalChunksRead, totalBytesRead);
        }

        /// <nodoc />
        public static async Task<(long totalChunkCount, long totalBytes)> CopyStreamToChunksAsync<T>(
            Stream input,
            IAsyncStreamWriter<T> output,
            Func<ByteString, long, T> transform,
            byte[] primaryBuffer,
            byte[] secondaryBuffer,
            Action<CopyStatistics>? progressReport = null,
            CancellationToken cancellationToken = default)
        {
            long totalChunksRead = 0L;
            long totalBytesRead = 0L;
            AsyncOut<TimeSpan> copyDuration = new AsyncOut<TimeSpan>();

            byte[] buffer = primaryBuffer;

            // Pre-fill buffer with the file's first chunk
            int chunkSize = await readNextChunk(input, buffer, cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (chunkSize == 0) { break; }

                // Created ByteString can reuse the buffer.
                // To avoid double writes we need two buffers:
                // * One buffer is used as the output buffer
                // * And another buffer is used as the input buffer.
                (ByteString content, bool bufferReused) = CreateByteStringForStreamContent(buffer, chunkSize);
                T response = transform(content, totalChunksRead);

                if (bufferReused)
                {
                    // If the buffer is reused we need to swap the input buffer with the secondary buffer.
                    buffer = buffer == primaryBuffer ? secondaryBuffer : primaryBuffer;
                }

                totalBytesRead += chunkSize;
                totalChunksRead++;

                // Read the next chunk while waiting for the response
                var readNextChunkTask = readNextChunk(input, buffer, cancellationToken);
                await Task.WhenAll(readNextChunkTask, MeasureCopyAsync(async () => { await output.WriteAsync(response); return 0; }, copyDuration));
                progressReport?.Invoke(new CopyStatistics(totalBytesRead, copyDuration.Value));

                chunkSize = await readNextChunkTask;
            }

            return (totalChunksRead, totalBytesRead);

            static async Task<int> readNextChunk(Stream input, byte[] buffer, CancellationToken token)
            {
                var bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, token);
                return bytesRead;
            }
        }

        public static async Task<T> MeasureCopyAsync<T>(Func<Task<T>> copyAsync, AsyncOut<TimeSpan> duration)
        {
            StopwatchSlim sw = StopwatchSlim.Start();
            try
            {
                return await copyAsync();
            }
            finally
            {
                duration.Value += sw.Elapsed;
            }
        }

        private static (ByteString content, bool bufferReused) CreateByteStringForStreamContent(byte[] buffer, int chunkSize)
        {
            // In some cases ByteString construction can be very expensive both in terms CPU and memory.
            // For instance, during the content streaming we understand and control the ownership of the buffers
            // and in that case we can avoid extra copy done by ByteString.CopyFrom call.
            //
            // But we can use an unsafe version only when the chunk size is the same as the buffer size.
            if (chunkSize == buffer.Length)
            {
                return (ByteStringExtensions.UnsafeCreateFromBytes(buffer), bufferReused: true);
            }

            return (ByteString.CopyFrom(buffer, 0, chunkSize), bufferReused: false);
        }

#pragma warning disable AsyncFixer01 // Unnecessary async/await usage. This is a false positive that happens only in .NET Core / NetStandard 2.1.
        /// <nodoc />
        public static async Task WriteByteStringAsync(this Stream stream, ByteString byteString, CancellationToken cancellationToken = default)
        {
            // Support for using Span in Stream's WriteAsync started in .NET Core 3.0 and .NET Standard 2.1. Since we
            // may run in older runtimes, we fallback into using the unsafe bytes extraction technique, whereby we
            // fetch the inner byte[] inside of the ByteString and write using that directly.
#if NETCOREAPP3_0 || NETCOREAPP3_1 || NETSTANDARD2_1
            await stream.WriteAsync(byteString.Memory, cancellationToken);
#else
            var buffer = ByteStringExtensions.UnsafeExtractBytes(byteString);
            await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
#endif
        }
#pragma warning restore AsyncFixer01 // Unnecessary async/await usage
    }
}
