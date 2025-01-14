﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.

namespace Nerdbank.Streams
{
    using System;
    using System.Buffers;
    using System.IO;
    using System.IO.Pipelines;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;

    /// <summary>
    /// Stream extension methods.
    /// </summary>
    public static partial class PipeExtensions
    {
        /// <summary>
        /// The default buffer size to use for pipe readers.
        /// </summary>
        private const int DefaultReadBufferSize = 4 * 1024;

        /// <summary>
        /// Exposes a full-duplex pipe as a <see cref="Stream"/>.
        /// The pipe will be completed when the <see cref="Stream"/> is disposed.
        /// </summary>
        /// <param name="pipe">The pipe to wrap as a stream.</param>
        /// <returns>The wrapping stream.</returns>
        public static Stream AsStream(this IDuplexPipe pipe) => new PipeStream(pipe, ownsPipe: true);

        /// <summary>
        /// Exposes a full-duplex pipe as a <see cref="Stream"/>.
        /// </summary>
        /// <param name="pipe">The pipe to wrap as a stream.</param>
        /// <param name="ownsPipe"><c>true</c> to complete the underlying reader and writer when the <see cref="Stream"/> is disposed; <c>false</c> to keep them open.</param>
        /// <returns>The wrapping stream.</returns>
        public static Stream AsStream(this IDuplexPipe pipe, bool ownsPipe) => new PipeStream(pipe, ownsPipe);

        /// <summary>
        /// Exposes a pipe reader as a <see cref="Stream"/>.
        /// </summary>
        /// <param name="pipeReader">The pipe to read from when <see cref="Stream.ReadAsync(byte[], int, int, CancellationToken)"/> is invoked.</param>
        /// <returns>The wrapping stream.</returns>
        /// <remarks>
        /// The reader will be completed when the <see cref="Stream"/> is disposed.
        /// </remarks>
        public static Stream AsStream(this PipeReader pipeReader) => new PipeStream(pipeReader, ownsPipe: true);

        /// <summary>
        /// Exposes a pipe writer as a <see cref="Stream"/>.
        /// </summary>
        /// <param name="pipeWriter">The pipe to write to when <see cref="Stream.WriteAsync(byte[], int, int, CancellationToken)"/> is invoked.</param>
        /// <returns>The wrapping stream.</returns>
        /// <remarks>
        /// The writer will be completed when the <see cref="Stream"/> is disposed.
        /// </remarks>
        public static Stream AsStream(this PipeWriter pipeWriter) => new PipeStream(pipeWriter, ownsPipe: true);

        /// <summary>
        /// Enables efficiently reading a stream using <see cref="PipeReader"/>.
        /// </summary>
        /// <param name="stream">The stream to read from using a pipe.</param>
        /// <param name="sizeHint">A hint at the size of messages that are commonly transferred. Use 0 for a commonly reasonable default.</param>
        /// <param name="pipeOptions">Optional pipe options to use.</param>
        /// <param name="cancellationToken">A cancellation token that aborts reading from the <paramref name="stream"/>.</param>
        /// <returns>A <see cref="PipeReader"/>.</returns>
        /// <remarks>
        /// When the caller invokes <see cref="PipeReader.Complete(Exception)"/> on the result value,
        /// this leads to the associated <see cref="PipeWriter.Complete(Exception)"/> to be automatically called as well.
        /// </remarks>
        public static PipeReader UsePipeReader(this Stream stream, int sizeHint = 0, PipeOptions pipeOptions = null, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(stream, nameof(stream));
            Requires.Argument(stream.CanRead, nameof(stream), "Stream must be readable.");

            var pipe = new Pipe(pipeOptions ?? PipeOptions.Default);

            // Notice when the pipe reader isn't listening any more, and terminate our loop that reads from the stream.
            var combinedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pipe.Writer.OnReaderCompleted((ex, state) => ((CancellationTokenSource)state).Cancel(), combinedTokenSource);

            Task.Run(async delegate
            {
                while (!combinedTokenSource.Token.IsCancellationRequested)
                {
                    Memory<byte> memory = pipe.Writer.GetMemory(sizeHint);
                    try
                    {
                        int bytesRead = await stream.ReadAsync(memory, combinedTokenSource.Token).ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        pipe.Writer.Advance(bytesRead);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Propagate the exception to the reader.
                        pipe.Writer.Complete(ex);
                        return;
                    }

                    FlushResult result = await pipe.Writer.FlushAsync().ConfigureAwait(false);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }

                // Tell the PipeReader that there's no more data coming
                pipe.Writer.Complete();
            }).Forget();
            return pipe.Reader;
        }

        /// <summary>
        /// Creates a <see cref="PipeReader"/> that reads from the specified <see cref="Stream"/> exactly as told to do so.
        /// </summary>
        /// <param name="stream">The stream to read from using a pipe.</param>
        /// <param name="sizeHint">A hint at the size of messages that are commonly transferred. Use 0 for a commonly reasonable default.</param>
        /// <returns>A <see cref="PipeReader"/>.</returns>
        /// <remarks>
        /// This reader may not be as efficient as the <see cref="Pipe"/>-based <see cref="PipeReader"/> returned from <see cref="UsePipeReader(Stream, int, PipeOptions, CancellationToken)"/>,
        /// but its interaction with the underlying <see cref="Stream"/> is closer to how a <see cref="Stream"/> would typically be used which can ease migration from streams to pipes.
        /// </remarks>
        public static PipeReader UseStrictPipeReader(this Stream stream, int sizeHint = DefaultReadBufferSize)
        {
            Requires.NotNull(stream, nameof(stream));
            Requires.Argument(stream.CanRead, nameof(stream), "Stream must be readable.");

            return new StreamPipeReader(stream, sizeHint);
        }

        /// <summary>
        /// Enables writing to a stream using <see cref="PipeWriter"/>.
        /// </summary>
        /// <param name="stream">The stream to write to using a pipe.</param>
        /// <param name="pipeOptions">Optional pipe options to use.</param>
        /// <param name="cancellationToken">A cancellation token that aborts writing to the <paramref name="stream"/>.</param>
        /// <returns>A <see cref="PipeWriter"/>.</returns>
        public static PipeWriter UsePipeWriter(this Stream stream, PipeOptions pipeOptions = null, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(stream, nameof(stream));
            Requires.Argument(stream.CanWrite, nameof(stream), "Stream must be writable.");

            var pipe = new Pipe(pipeOptions ?? PipeOptions.Default);
            Task.Run(async delegate
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        ReadResult readResult = await pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        if (readResult.Buffer.Length > 0)
                        {
                            foreach (ReadOnlyMemory<byte> segment in readResult.Buffer)
                            {
                                await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                            }

                            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        pipe.Reader.AdvanceTo(readResult.Buffer.End);

                        if (readResult.IsCompleted)
                        {
                            break;
                        }
                    }

                    pipe.Reader.Complete();
                }
                catch (Exception ex)
                {
                    // Propagate the exception to the writer.
                    pipe.Reader.Complete(ex);
                    return;
                }
            }).Forget();
            return pipe.Writer;
        }

        /// <summary>
        /// Creates a <see cref="PipeWriter"/> that writes to an underlying <see cref="Stream"/>
        /// when <see cref="PipeWriter.FlushAsync(CancellationToken)"/> is called rather than asynchronously sometime later.
        /// </summary>
        /// <param name="stream">The stream to write to using a pipe.</param>
        /// <returns>A <see cref="PipeWriter"/>.</returns>
        /// <remarks>
        /// This writer may not be as efficient as the <see cref="Pipe"/>-based <see cref="PipeWriter"/> returned from <see cref="UsePipeWriter(Stream, PipeOptions, CancellationToken)"/>,
        /// but its interaction with the underlying <see cref="Stream"/> is closer to how a <see cref="Stream"/> would typically be used which can ease migration from streams to pipes.
        /// </remarks>
        public static PipeWriter UseStrictPipeWriter(this Stream stream)
        {
            Requires.NotNull(stream, nameof(stream));
            Requires.Argument(stream.CanWrite, nameof(stream), "Stream must be writable.");

            return new StreamPipeWriter(stream);
        }

        /// <summary>
        /// Enables reading and writing to a <see cref="Stream"/> using <see cref="PipeWriter"/> and <see cref="PipeReader"/>.
        /// </summary>
        /// <param name="stream">The stream to access using a pipe.</param>
        /// <param name="sizeHint">A hint at the size of messages that are commonly transferred. Use 0 for a commonly reasonable default.</param>
        /// <param name="pipeOptions">Optional pipe options to use.</param>
        /// <param name="cancellationToken">A token that may cancel async processes to read from and write to the <paramref name="stream"/>.</param>
        /// <returns>An <see cref="IDuplexPipe"/> instance.</returns>
        public static IDuplexPipe UsePipe(this Stream stream, int sizeHint = 0, PipeOptions pipeOptions = null, CancellationToken cancellationToken = default)
        {
            PipeReader input = stream.UsePipeReader(sizeHint, pipeOptions, cancellationToken);
            PipeWriter output = stream.UsePipeWriter(pipeOptions, cancellationToken);

            // Arrange for closing the stream when *both* input/output are completed.
            Task ioCompleted = Task.WhenAll(input.WaitForWriterCompletionAsync(), output.WaitForReaderCompletionAsync());
            ioCompleted.ContinueWith((_, state) => ((Stream)state).Dispose(), stream, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default).Forget();

            return new DuplexPipe(input, output);
        }

        /// <summary>
        /// Enables efficiently reading a <see cref="WebSocket"/> using <see cref="PipeReader"/>.
        /// </summary>
        /// <param name="webSocket">The web socket to read from using a pipe.</param>
        /// <param name="sizeHint">A hint at the size of messages that are commonly transferred. Use 0 for a commonly reasonable default.</param>
        /// <param name="pipeOptions">Optional pipe options to use.</param>
        /// <param name="cancellationToken">A cancellation token that aborts reading from the <paramref name="webSocket"/>.</param>
        /// <returns>A <see cref="PipeReader"/>.</returns>
        public static PipeReader UsePipeReader(this WebSocket webSocket, int sizeHint = 0, PipeOptions pipeOptions = null, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(webSocket, nameof(webSocket));

            var pipe = new Pipe(pipeOptions ?? PipeOptions.Default);
            Task.Run(async delegate
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Memory<byte> memory = pipe.Writer.GetMemory(sizeHint);
                    try
                    {
                        var readResult = await webSocket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
                        if (readResult.Count == 0)
                        {
                            break;
                        }

                        pipe.Writer.Advance(readResult.Count);
                    }
                    catch (Exception ex)
                    {
                        // Propagate the exception to the reader.
                        pipe.Writer.Complete(ex);
                        return;
                    }

                    FlushResult result = await pipe.Writer.FlushAsync().ConfigureAwait(false);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }

                // Tell the PipeReader that there's no more data coming
                pipe.Writer.Complete();
            }).Forget();

            return pipe.Reader;
        }

        /// <summary>
        /// Enables efficiently writing to a <see cref="WebSocket"/> using a <see cref="PipeWriter"/>.
        /// </summary>
        /// <param name="webSocket">The web socket to write to using a pipe.</param>
        /// <param name="pipeOptions">Optional pipe options to use.</param>
        /// <param name="cancellationToken">A cancellation token that aborts writing to the <paramref name="webSocket"/>.</param>
        /// <returns>A <see cref="PipeWriter"/>.</returns>
        public static PipeWriter UsePipeWriter(this WebSocket webSocket, PipeOptions pipeOptions = null, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(webSocket, nameof(webSocket));

            var pipe = new Pipe(pipeOptions ?? PipeOptions.Default);
            Task.Run(async delegate
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        ReadResult readResult = await pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        if (readResult.Buffer.Length > 0)
                        {
                            foreach (ReadOnlyMemory<byte> segment in readResult.Buffer)
                            {
                                await webSocket.SendAsync(segment, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        pipe.Reader.AdvanceTo(readResult.Buffer.End);

                        if (readResult.IsCompleted)
                        {
                            break;
                        }
                    }

                    pipe.Reader.Complete();
                }
                catch (Exception ex)
                {
                    // Propagate the exception to the writer.
                    pipe.Reader.Complete(ex);
                    return;
                }
            }).Forget();
            return pipe.Writer;
        }

        /// <summary>
        /// Enables reading and writing to a <see cref="WebSocket"/> using <see cref="PipeWriter"/> and <see cref="PipeReader"/>.
        /// </summary>
        /// <param name="webSocket">The <see cref="WebSocket"/> to access using a pipe.</param>
        /// <param name="sizeHint">A hint at the size of messages that may be transferred. Use 0 for a commonly reasonable default.</param>
        /// <param name="pipeOptions">Optional pipe options to use.</param>
        /// <param name="cancellationToken">A token that may cancel async processes to read from and write to the <paramref name="webSocket"/>.</param>
        /// <returns>An <see cref="IDuplexPipe"/> instance.</returns>
        public static IDuplexPipe UsePipe(this WebSocket webSocket, int sizeHint = 0, PipeOptions pipeOptions = null, CancellationToken cancellationToken = default)
        {
            return new DuplexPipe(webSocket.UsePipeReader(sizeHint, pipeOptions, cancellationToken), webSocket.UsePipeWriter(pipeOptions, cancellationToken));
        }

        /// <summary>
        /// Forwards all bytes coming from a <see cref="PipeReader"/> to the specified <see cref="PipeWriter"/>.
        /// </summary>
        /// <param name="reader">The reader to get bytes from.</param>
        /// <param name="writer">The writer to copy bytes to.</param>
        /// <param name="propagateSuccessfulCompletion"><c>true</c> to complete the <paramref name="writer"/> when <paramref name="reader"/> completes.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>
        /// A <see cref="Task"/> that completes when the <paramref name="reader"/> has finished producing bytes, or an error occurs.
        /// This <see cref="Task"/> never faults, since any exceptions are used to complete the <paramref name="writer"/>.
        /// </returns>
        /// <remarks>
        /// If an error occurs during reading or writing, the <paramref name="writer"/> is completed with the exception.
        /// </remarks>
        internal static Task LinkToAsync(this PipeReader reader, PipeWriter writer, bool propagateSuccessfulCompletion, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(reader, nameof(reader));
            Requires.NotNull(writer, nameof(writer));

            return Task.Run(async delegate
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        writer.Write(result.Buffer);
                        reader.AdvanceTo(result.Buffer.End);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (result.IsCompleted)
                        {
                            if (propagateSuccessfulCompletion)
                            {
                                writer.Complete();
                            }

                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    writer.Complete(ex);
                }
            });
        }

        /// <summary>
        /// Copies a sequence of bytes to a <see cref="PipeWriter"/>.
        /// </summary>
        /// <param name="writer">The writer to use.</param>
        /// <param name="sequence">The sequence to read.</param>
        private static void Write(this PipeWriter writer, ReadOnlySequence<byte> sequence)
        {
            Requires.NotNull(writer, nameof(writer));

            foreach (ReadOnlyMemory<byte> sourceMemory in sequence)
            {
                var sourceSpan = sourceMemory.Span;
                var targetSpan = writer.GetSpan(sourceSpan.Length);
                sourceSpan.CopyTo(targetSpan);
                writer.Advance(sourceSpan.Length);
            }
        }
    }
}
