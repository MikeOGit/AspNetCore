// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Adapter.Internal
{
    internal class AdaptedPipeline : IDuplexPipe, IDisposable
    {
        private readonly int _minAllocBufferSize;

        private readonly IDuplexPipe _transport;

        public AdaptedPipeline(IDuplexPipe transport,
                               Pipe inputPipe,
                               Pipe outputPipe,
                               ILogger log,
                               int minAllocBufferSize)
        {
            _transport = transport;
            Input = inputPipe;
            Output = outputPipe;
            Log = log;
            _minAllocBufferSize = minAllocBufferSize;
        }

        public Pipe Input { get; }

        public Pipe Output { get; }

        public ILogger Log { get; }

        PipeReader IDuplexPipe.Input => Input.Reader;

        PipeWriter IDuplexPipe.Output => Output.Writer;

        public async Task RunAsync(Stream stream)
        {
            var inputTask = ReadInputAsync(stream);
            var outputTask = WriteOutputAsync(stream);

            await inputTask;
            await outputTask;
        }

        private async Task WriteOutputAsync(Stream stream)
        {
            try
            {
                if (stream == null)
                {
                    return;
                }

                while (true)
                {
                    var result = await Output.Reader.ReadAsync();
                    var buffer = result.Buffer;

                    try
                    {
                        if (result.IsCanceled)
                        {
                            break;
                        }

                        if (buffer.IsEmpty)
                        {
                            if (result.IsCompleted)
                            {
                                break;
                            }
                            await stream.FlushAsync();
                        }
                        else if (buffer.IsSingleSegment)
                        {
                            await stream.WriteAsync(buffer.First);
                        }
                        else
                        {
                            foreach (var memory in buffer)
                            {
                                await stream.WriteAsync(memory);
                            }
                        }
                    }
                    finally
                    {
                        Output.Reader.AdvanceTo(buffer.End);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError(0, ex, $"{nameof(AdaptedPipeline)}.{nameof(WriteOutputAsync)}");
            }
            finally
            {
                Output.Reader.Complete();

                _transport.Output.Complete();

                // Cancel any pending flushes due to back-pressure
                Input.Writer.CancelPendingFlush();
            }
        }

        private async Task ReadInputAsync(Stream stream)
        {
            Exception error = null;

            try
            {
                if (stream == null)
                {
                    // REVIEW: Do we need an exception here?
                    return;
                }

                while (true)
                {

                    var outputBuffer = Input.Writer.GetMemory(_minAllocBufferSize);
                    var bytesRead = await stream.ReadAsync(outputBuffer);
                    Input.Writer.Advance(bytesRead);

                    if (bytesRead == 0)
                    {
                        // FIN
                        break;
                    }

                    var result = await Input.Writer.FlushAsync();

                    if (result.IsCompleted || result.IsCanceled)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Don't rethrow the exception. It should be handled by the Pipeline consumer.
                error = ex;
            }
            finally
            {
                Input.Writer.Complete(error);
                // The application could have ended the input pipe so complete
                // the transport pipe as well
                _transport.Input.Complete();

                // Cancel any pending reads from the application
                Output.Reader.CancelPendingRead();
            }
        }

        public void Dispose()
        {
            Input.Reader.Complete();
            Output.Writer.Complete();
        }
    }
}
