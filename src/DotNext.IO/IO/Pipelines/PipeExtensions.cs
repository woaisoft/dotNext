﻿using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Missing = System.Reflection.Missing;

namespace DotNext.IO.Pipelines
{
    using Buffers;
    using Security.Cryptography;
    using Text;

    /// <summary>
    /// Represents extension method for parsing data stored in pipe.
    /// </summary>
    public static class PipeExtensions
    {
        [StructLayout(LayoutKind.Auto)]
        private struct HashReader : IBufferReader<HashBuilder>
        {
            private readonly HashBuilder builder;
            private readonly bool limited;
            private int remainingBytes;

            internal HashReader(HashAlgorithm algorithm, int? count)
            {
                builder = new HashBuilder(algorithm);
                if (count.HasValue)
                {
                    limited = true;
                    remainingBytes = count.GetValueOrDefault();
                }
                else
                {
                    limited = false;
                    remainingBytes = 4096;
                }
            }

            readonly int IBufferReader<HashBuilder>.RemainingBytes => remainingBytes;

            readonly HashBuilder IBufferReader<HashBuilder>.Complete() => builder;

            void IBufferReader<HashBuilder>.EndOfStream()
                => remainingBytes = limited ? throw new EndOfStreamException() : 0;

            void IBufferReader<HashBuilder>.Append(ReadOnlySpan<byte> block, ref int consumedBytes)
            {
                builder.Add(block);
                if (limited)
                    remainingBytes -= block.Length;
            }
        }

        private static async ValueTask<TResult> ReadAsync<TResult, TParser>(this PipeReader reader, TParser parser, CancellationToken token)
            where TParser : struct, IBufferReader<TResult>
        {
            for (SequencePosition consumed; parser.RemainingBytes > 0; reader.AdvanceTo(consumed))
            {
                var readResult = await reader.ReadAsync(token).ConfigureAwait(false);
                readResult.ThrowIfCancellationRequested(token);
                parser.Append<TResult, TParser>(readResult.Buffer, out consumed);
            }

            return parser.Complete();
        }

        internal static async ValueTask ComputeHashAsync(this PipeReader reader, HashAlgorithm algorithm, int? count, Memory<byte> output, CancellationToken token)
        {
            using var builder = await reader.ReadAsync<HashBuilder, HashReader>(new HashReader(algorithm, count), token).ConfigureAwait(false);
            builder.Build(output.Span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ValueTask<int> Read7BitEncodedIntAsync(this PipeReader reader, CancellationToken token)
            => reader.ReadAsync<int, SevenBitEncodedIntReader>(new SevenBitEncodedIntReader(5), token);

        /// <summary>
        /// Decodes string asynchronously from pipe.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="length">The length of the string, in bytes.</param>
        /// <param name="context">The text decoding context.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The decoded string.</returns>
        /// <exception cref="EndOfStreamException"><paramref name="reader"/> doesn't contain the necessary number of bytes to restore string.</exception>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public static async ValueTask<string> ReadStringAsync(this PipeReader reader, int length, DecodingContext context, CancellationToken token = default)
        {
            if (length == 0)
                return string.Empty;
            using var resultBuffer = new ArrayBuffer<char>(length);
            return await ReadAsync<string, StringReader<ArrayBuffer<char>>>(reader, new StringReader<ArrayBuffer<char>>(context, resultBuffer), token).ConfigureAwait(false);
        }

        private static async ValueTask<int> ReadLengthAsync(this PipeReader reader, StringLengthEncoding lengthFormat, CancellationToken token)
        {
            ValueTask<int> result;
            var littleEndian = BitConverter.IsLittleEndian;
            switch (lengthFormat)
            {
                default:
                    throw new ArgumentOutOfRangeException(nameof(lengthFormat));
                case StringLengthEncoding.Plain:
                    result = reader.ReadAsync<int>(token);
                    break;
                case StringLengthEncoding.PlainLittleEndian:
                    littleEndian = true;
                    goto case StringLengthEncoding.Plain;
                case StringLengthEncoding.PlainBigEndian:
                    littleEndian = false;
                    goto case StringLengthEncoding.Plain;
                case StringLengthEncoding.Compressed:
                    result = reader.Read7BitEncodedIntAsync(token);
                    break;
            }

            var length = await result.ConfigureAwait(false);
            length.ReverseIfNeeded(littleEndian);
            return length;
        }

        /// <summary>
        /// Decodes string asynchronously from pipe.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="lengthFormat">Represents string length encoding format.</param>
        /// <param name="context">The text decoding context.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The decoded string.</returns>
        /// <exception cref="EndOfStreamException"><paramref name="reader"/> doesn't contain the necessary number of bytes to restore string.</exception>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthFormat"/> is invalid.</exception>
        public static async ValueTask<string> ReadStringAsync(this PipeReader reader, StringLengthEncoding lengthFormat, DecodingContext context, CancellationToken token = default)
            => await ReadStringAsync(reader, await reader.ReadLengthAsync(lengthFormat, token).ConfigureAwait(false), context, token).ConfigureAwait(false);

        /// <summary>
        /// Reads value of blittable type from pipe.
        /// </summary>
        /// <typeparam name="T">The blittable type to decode.</typeparam>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="token">The token that can be used to cancel operation.</param>
        /// <returns>The decoded value.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public static ValueTask<T> ReadAsync<T>(this PipeReader reader, CancellationToken token = default)
            where T : unmanaged
            => ReadAsync<T, ValueReader<T>>(reader, new ValueReader<T>(), token);

        /// <summary>
        /// Decodes 64-bit signed integer using the specified endianness.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="littleEndian"><see langword="true"/> if value is stored in the underlying binary stream as little-endian; otherwise, use big-endian.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The decoded value.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="EndOfStreamException">The underlying source doesn't contain necessary amount of bytes to decode the value.</exception>
        public static async ValueTask<long> ReadInt64Async(this PipeReader reader, bool littleEndian, CancellationToken token = default)
        {
            var result = await reader.ReadAsync<long>(token).ConfigureAwait(false);
            result.ReverseIfNeeded(littleEndian);
            return result;
        }

        /// <summary>
        /// Decodes 64-bit unsigned integer using the specified endianness.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="littleEndian"><see langword="true"/> if value is stored in the underlying binary stream as little-endian; otherwise, use big-endian.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The decoded value.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="EndOfStreamException">The underlying source doesn't contain necessary amount of bytes to decode the value.</exception>
        [CLSCompliant(false)]
        public static async ValueTask<ulong> ReadUInt64Async(this PipeReader reader, bool littleEndian, CancellationToken token = default)
        {
            var result = await reader.ReadAsync<ulong>(token).ConfigureAwait(false);
            result.ReverseIfNeeded(littleEndian);
            return result;
        }

        /// <summary>
        /// Decodes 32-bit signed integer using the specified endianness.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="littleEndian"><see langword="true"/> if value is stored in the underlying binary stream as little-endian; otherwise, use big-endian.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The decoded value.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="EndOfStreamException">The underlying source doesn't contain necessary amount of bytes to decode the value.</exception>
        public static async ValueTask<int> ReadInt32Async(this PipeReader reader, bool littleEndian, CancellationToken token = default)
        {
            var result = await reader.ReadAsync<int>(token).ConfigureAwait(false);
            result.ReverseIfNeeded(littleEndian);
            return result;
        }

        /// <summary>
        /// Decodes 32-bit unsigned integer using the specified endianness.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="littleEndian"><see langword="true"/> if value is stored in the underlying binary stream as little-endian; otherwise, use big-endian.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The decoded value.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="EndOfStreamException">The underlying source doesn't contain necessary amount of bytes to decode the value.</exception>
        [CLSCompliant(false)]
        public static async ValueTask<uint> ReadUInt32Async(this PipeReader reader, bool littleEndian, CancellationToken token = default)
        {
            var result = await reader.ReadAsync<uint>(token).ConfigureAwait(false);
            result.ReverseIfNeeded(littleEndian);
            return result;
        }

        /// <summary>
        /// Decodes 16-bit signed integer using the specified endianness.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="littleEndian"><see langword="true"/> if value is stored in the underlying binary stream as little-endian; otherwise, use big-endian.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The decoded value.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="EndOfStreamException">The underlying source doesn't contain necessary amount of bytes to decode the value.</exception>
        public static async ValueTask<short> ReadInt16Async(this PipeReader reader, bool littleEndian, CancellationToken token = default)
        {
            var result = await reader.ReadAsync<short>(token).ConfigureAwait(false);
            result.ReverseIfNeeded(littleEndian);
            return result;
        }

        /// <summary>
        /// Decodes 16-bit signed integer using the specified endianness.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="littleEndian"><see langword="true"/> if value is stored in the underlying binary stream as little-endian; otherwise, use big-endian.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The decoded value.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="EndOfStreamException">The underlying source doesn't contain necessary amount of bytes to decode the value.</exception>
        [CLSCompliant(false)]
        public static async ValueTask<ushort> ReadUInt16Async(this PipeReader reader, bool littleEndian, CancellationToken token = default)
        {
            var result = await reader.ReadAsync<ushort>(token).ConfigureAwait(false);
            result.ReverseIfNeeded(littleEndian);
            return result;
        }

        /// <summary>
        /// Reads the block of memory.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="output">The block of memory to fill from the pipe.</param>
        /// <param name="token">The token that can be used to cancel operation.</param>
        /// <returns>The task representing asynchronous state of the operation.</returns>
        /// <exception cref="EndOfStreamException">Reader doesn't have enough data.</exception>
        public static async ValueTask ReadBlockAsync(this PipeReader reader, Memory<byte> output, CancellationToken token = default)
            => await ReadAsync<Missing, MemoryReader>(reader, new MemoryReader(output), token).ConfigureAwait(false);

        /// <summary>
        /// Reads the block of memory.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="output">The block of memory to fill from the pipe.</param>
        /// <param name="token">The token that can be used to cancel operation.</param>
        /// <returns>The task representing asynchronous state of the operation.</returns>
        /// <exception cref="EndOfStreamException">Reader doesn't have enough data.</exception>
        [Obsolete("Use ReadBlockAsync extension method instead")]
        public static ValueTask ReadAsync(this PipeReader reader, Memory<byte> output, CancellationToken token = default)
            => ReadBlockAsync(reader, output, token);

        /// <summary>
        /// Reads the block of memory.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="output">The block of memory to fill from the pipe.</param>
        /// <param name="token">The token that can be used to cancel operation.</param>
        /// <returns>The actual number of copied bytes.</returns>
        public static ValueTask<int> CopyToAsync(this PipeReader reader, Memory<byte> output, CancellationToken token = default)
            => ReadAsync<int, MemoryReader>(reader, new MemoryReader(output), token);

        /// <summary>
        /// Encodes value of blittable type.
        /// </summary>
        /// <typeparam name="T">The blittable type to encode.</typeparam>
        /// <param name="writer">The pipe writer.</param>
        /// <param name="value">The value to be encoded in binary form.</param>
        /// <param name="token">The token that can be used to cancel operation.</param>
        /// <returns>The task representing asynchronous result of operation.</returns>
        public static ValueTask<FlushResult> WriteAsync<T>(this PipeWriter writer, T value, CancellationToken token = default)
            where T : unmanaged
        {
            writer.Write(in value);
            return writer.FlushAsync(token);
        }

        /// <summary>
        /// Encodes 64-bit signed integer asynchronously.
        /// </summary>
        /// <param name="writer">The pipe writer.</param>
        /// <param name="value">The value to encode.</param>
        /// <param name="littleEndian"><see langword="true"/> to use little-endian encoding; <see langword="false"/> to use big-endian encoding.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing state of asynchronous execution.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public static ValueTask<FlushResult> WriteInt64Async(this PipeWriter writer, long value, bool littleEndian, CancellationToken token = default)
        {
            writer.WriteInt64(value, littleEndian);
            return writer.FlushAsync(token);
        }

        /// <summary>
        /// Encodes 64-bit unsigned integer asynchronously.
        /// </summary>
        /// <param name="writer">The pipe writer.</param>
        /// <param name="value">The value to encode.</param>
        /// <param name="littleEndian"><see langword="true"/> to use little-endian encoding; <see langword="false"/> to use big-endian encoding.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing state of asynchronous execution.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        [CLSCompliant(false)]
        public static ValueTask<FlushResult> WriteUInt64Async(this PipeWriter writer, ulong value, bool littleEndian, CancellationToken token = default)
        {
            writer.WriteUInt64(value, littleEndian);
            return writer.FlushAsync(token);
        }

        /// <summary>
        /// Encodes 32-bit signed integer asynchronously.
        /// </summary>
        /// <param name="writer">The pipe writer.</param>
        /// <param name="value">The value to encode.</param>
        /// <param name="littleEndian"><see langword="true"/> to use little-endian encoding; <see langword="false"/> to use big-endian encoding.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing state of asynchronous execution.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public static ValueTask<FlushResult> WriteInt32Async(this PipeWriter writer, int value, bool littleEndian, CancellationToken token = default)
        {
            writer.WriteInt32(value, littleEndian);
            return writer.FlushAsync(token);
        }

        /// <summary>
        /// Encodes 32-bit unsigned integer asynchronously.
        /// </summary>
        /// <param name="writer">The pipe writer.</param>
        /// <param name="value">The value to encode.</param>
        /// <param name="littleEndian"><see langword="true"/> to use little-endian encoding; <see langword="false"/> to use big-endian encoding.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing state of asynchronous execution.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        [CLSCompliant(false)]
        public static ValueTask<FlushResult> WriteUInt32Async(this PipeWriter writer, uint value, bool littleEndian, CancellationToken token = default)
        {
            writer.WriteUInt32(value, littleEndian);
            return writer.FlushAsync(token);
        }

        /// <summary>
        /// Encodes 16-bit signed integer asynchronously.
        /// </summary>
        /// <param name="writer">The pipe writer.</param>
        /// <param name="value">The value to encode.</param>
        /// <param name="littleEndian"><see langword="true"/> to use little-endian encoding; <see langword="false"/> to use big-endian encoding.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing state of asynchronous execution.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public static ValueTask<FlushResult> WriteInt16Async(this PipeWriter writer, short value, bool littleEndian, CancellationToken token = default)
        {
            writer.WriteInt16(value, littleEndian);
            return writer.FlushAsync(token);
        }

        /// <summary>
        /// Encodes 16-bit unsigned integer asynchronously.
        /// </summary>
        /// <param name="writer">The pipe writer.</param>
        /// <param name="value">The value to encode.</param>
        /// <param name="littleEndian"><see langword="true"/> to use little-endian encoding; <see langword="false"/> to use big-endian encoding.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing state of asynchronous execution.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        [CLSCompliant(false)]
        public static ValueTask<FlushResult> WriteUInt16Async(this PipeWriter writer, ushort value, bool littleEndian, CancellationToken token = default)
        {
            writer.WriteUInt16(value, littleEndian);
            return writer.FlushAsync(token);
        }

        private static ValueTask<FlushResult> WriteLengthAsync(this PipeWriter writer, ReadOnlyMemory<char> value, Encoding encoding, StringLengthEncoding? lengthFormat, CancellationToken token)
        {
            if (lengthFormat is null)
                return new ValueTask<FlushResult>(new FlushResult(false, false));

            writer.WriteLength(value.Span, encoding, lengthFormat.GetValueOrDefault());
            return writer.FlushAsync(token);
        }

        /// <summary>
        /// Encodes the string to bytes and write them to pipe asynchronously.
        /// </summary>
        /// <param name="writer">The pipe writer.</param>
        /// <param name="value">The block of characters to encode.</param>
        /// <param name="context">The text encoding context.</param>
        /// <param name="bufferSize">The buffer size (in bytes) used for encoding.</param>
        /// <param name="lengthFormat">String length encoding format; or <see langword="null"/> to prevent encoding of string length.</param>
        /// <param name="token">The token that can be used to cancel operation.</param>
        /// <returns>The result of operation.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthFormat"/> is invalid.</exception>
        /// <exception cref="EndOfStreamException">Pipe closed unexpectedly.</exception>
        public static async ValueTask WriteStringAsync(this PipeWriter writer, ReadOnlyMemory<char> value, EncodingContext context, int bufferSize = 0, StringLengthEncoding? lengthFormat = null, CancellationToken token = default)
        {
            var result = await writer.WriteLengthAsync(value, context.Encoding, lengthFormat, token).ConfigureAwait(false);
            result.ThrowIfCancellationRequested(token);
            if (value.IsEmpty)
                return;
            var encoder = context.GetEncoder();
            for (int charsLeft = value.Length, charsUsed, maxChars, bytesPerChar = context.Encoding.GetMaxByteCount(1); charsLeft > 0; value = value.Slice(charsUsed), charsLeft -= charsUsed)
            {
                if (result.IsCompleted)
                    throw new EndOfStreamException();
                var buffer = writer.GetMemory(bufferSize);
                maxChars = buffer.Length / bytesPerChar;
                charsUsed = Math.Min(maxChars, charsLeft);
                encoder.Convert(value.Span.Slice(0, charsUsed), buffer.Span, charsUsed == charsLeft, out charsUsed, out var bytesUsed, out _);
                writer.Advance(bytesUsed);
                result = await writer.FlushAsync(token).ConfigureAwait(false);
                result.ThrowIfCancellationRequested(token);
            }
        }

        /// <summary>
        /// Writes sequence of bytes to the underlying stream asynchronously.
        /// </summary>
        /// <param name="writer">The pipe to write into.</param>
        /// <param name="sequence">The sequence of bytes.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The actual number of bytes written to the pipe.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public static async ValueTask<long> WriteAsync(this PipeWriter writer, ReadOnlySequence<byte> sequence, CancellationToken token)
        {
            var count = 0L;
            var flushResult = new FlushResult(false, false);

            for (var position = sequence.Start; !flushResult.IsCompleted && sequence.TryGet(ref position, out var block); count += block.Length, flushResult.ThrowIfCancellationRequested(token))
            {
                flushResult = await writer.WriteAsync(block, token).ConfigureAwait(false);
            }

            return count;
        }
    }
}
