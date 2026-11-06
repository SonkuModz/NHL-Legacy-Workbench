using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>
/// Parses EA's "ABKC" audio bank container (used for NHL Legacy Edition's goal horns,
/// chants, and other short SFX/music banks) down to the S10A inner bank's per-sound table.
///
/// Reverse-engineered by decompiling the user's own PS3-only ABK_Insert.exe/.dll (which
/// only ever needed to understand the EA-XAS/S10A pairing) and cross-checking every field
/// against real PS3 and Xbox 360 sample files byte-for-byte. All multi-byte outer/S10A
/// header fields are big-endian; <see cref="BigEndianReader"/> reads them directly (the
/// decompiled tool instead does a little-endian read followed by a manual byte-swap --
/// mathematically identical, this is just the more direct expression of it).
///
/// One structural detail is empirical rather than derived from the tool's own logic: for
/// codec 3 (Xbox 360 XMA2) sounds, the real audio payload starts 4 bytes after where the
/// generic chunk-size/chunk-sample-count header would suggest. This was confirmed by
/// successfully decoding a real 360 sample only once that 4-byte gap was skipped; codec 4
/// (EA-XAS) sounds have no such gap. Only ever observed on a single-chunk sound of each
/// codec -- if a multi-chunk sound turns up, the second-and-later chunks' framing is
/// unverified.
/// </summary>
public static class AbkContainer
{
    private const uint AbkcMagic = 0x41424B43; // "ABKC"
    private const uint S10AMagic = 0x53313041; // "S10A"
    private const uint DummyOffset = 0xFFFFFFFF;

    public static bool IsAbkc(byte[] data) =>
        data.Length >= 4 && BigEndianReader.ReadUInt32BEAt(data, 0) == AbkcMagic;

    public static Result<AbkFile> Parse(byte[] data)
    {
        try
        {
            if (!IsAbkc(data))
            {
                return Result.Failure<AbkFile>("Not an ABKC audio bank (magic mismatch).");
            }

            if (data.Length < 60)
            {
                return Result.Failure<AbkFile>("File is too small to contain an ABKC header.");
            }

            uint innerBankOffset = BigEndianReader.ReadUInt32BEAt(data, 24); // "num2"
            if (innerBankOffset + 4 > data.Length)
            {
                return Result.Failure<AbkFile>("ABKC inner bank offset is out of range.");
            }

            uint innerMagic = BigEndianReader.ReadUInt32BEAt(data, (int)innerBankOffset);
            if (innerMagic != S10AMagic)
            {
                return Result.Failure<AbkFile>($"Unsupported inner audio bank format (expected S10A, found 0x{innerMagic:X8}). Only the S10A bank format used by NHL Legacy Edition is supported.");
            }

            long bankStart = innerBankOffset;
            uint soundCount = BigEndianReader.ReadUInt32BEAt(data, (int)(bankStart + 8));

            var sounds = new List<AbkSound>();
            for (int i = 0; i < soundCount; i++)
            {
                long offsetTablePos = bankStart + 12 + (i * 4);
                if (offsetTablePos + 4 > data.Length)
                {
                    break;
                }

                uint soundOffset = BigEndianReader.ReadUInt32BEAt(data, (int)offsetTablePos);
                if (soundOffset == DummyOffset)
                {
                    sounds.Add(new AbkSound { Index = i, IsDummy = true });
                    continue;
                }

                long headerPos = bankStart + soundOffset;
                Result<AbkSound> soundResult = ParseSound(data, i, headerPos);
                if (!soundResult.IsSuccess)
                {
                    return Result.Failure<AbkFile>($"Sound {i}: {soundResult.Error}");
                }

                sounds.Add(soundResult.Value!);
            }

            return Result.Success(new AbkFile(data, bankStart, sounds));
        }
        catch (Exception ex)
        {
            return Result.Failure<AbkFile>($"Failed to parse ABKC file: {ex.Message}", ex);
        }
    }

    private static Result<AbkSound> ParseSound(byte[] data, int index, long headerPos)
    {
        if (headerPos + 8 > data.Length)
        {
            return Result.Failure<AbkSound>("Per-sound header is out of range.");
        }

        // Codec/sample-rate/channel packed word. Layout confirmed against the decompiled
        // insertion tool's own write-side formula (num5 = codec | byte_swap(sampleRate) |
        // (channels-1)<<10) and validated by cross-checking it decodes both real samples to
        // clean, round values (44100 Hz on both; stereo on PS3, mono on 360).
        //
        // The codec nibble and loop-flags nibble below are read from the *raw little-endian*
        // interpretation of these words, not the big-endian one -- this mirrors the decompiled
        // tool exactly, which reads via .NET's little-endian BinaryReader.ReadUInt32() and only
        // byte-swaps the parts of the value it treats as a real (big-endian-on-disk) number.
        // Reading these two words as big-endian (as every other field in this format is) would
        // pull the codec/flag bits from the wrong byte entirely.
        uint packed = ReadUInt32LEAt(data, (int)headerPos);
        int codecId = (int)(packed & 0xF);
        int channels = (int)((packed >> 10) & 0x3) + 1;
        uint sampleRateSwapped = packed & 0xFFFFF000;
        int sampleRate = (int)ByteSwap(sampleRateSwapped);

        uint flagsAndCount = ReadUInt32LEAt(data, (int)(headerPos + 4));
        byte loopFlags = (byte)(flagsAndCount & 0xF0);
        uint totalSamples = ByteSwap(flagsAndCount & 0xFFFFFF0F);

        long cursor = headerPos + 8;
        if ((loopFlags & 0x20) != 0)
        {
            cursor += 4; // loop start (unused by this reader)
            if ((loopFlags & 0x40) != 0)
            {
                cursor += 4; // loop length (unused by this reader)
            }
        }

        // Chunk table: repeating [chunkSize:u32][chunkSampleCount:u32] pairs, chunkSize
        // inclusive of its own 8-byte header. Every real sample seen so far has exactly one
        // chunk holding the whole sound.
        long payloadOffset = -1;
        long payloadLength = 0;
        uint accumulatedSamples = 0;
        bool firstChunk = true;

        while (accumulatedSamples < totalSamples)
        {
            if (cursor + 8 > data.Length)
            {
                return Result.Failure<AbkSound>("Chunk table ran past the end of the file.");
            }

            uint chunkSize = BigEndianReader.ReadUInt32BEAt(data, (int)cursor);
            uint chunkSampleCount = BigEndianReader.ReadUInt32BEAt(data, (int)(cursor + 4));
            accumulatedSamples += chunkSampleCount;

            long dataStart = cursor + 8;
            long dataLength = chunkSize - 8;

            if (firstChunk && codecId == (int)AbkCodecKind.Xma2)
            {
                // Empirically-derived: see the type-level doc comment.
                dataStart += 4;
                dataLength -= 4;
            }

            if (firstChunk)
            {
                payloadOffset = dataStart;
            }

            payloadLength += dataLength;
            cursor += chunkSize;
            firstChunk = false;

            if (chunkSize < 8)
            {
                return Result.Failure<AbkSound>("Malformed chunk (size smaller than its own header).");
            }
        }

        var codec = Enum.IsDefined(typeof(AbkCodecKind), codecId) ? (AbkCodecKind)codecId : AbkCodecKind.Unknown;

        return Result.Success(new AbkSound
        {
            Index = index,
            IsDummy = false,
            Codec = codec,
            CodecId = codecId,
            SampleRate = sampleRate,
            Channels = channels,
            SampleCount = (int)totalSamples,
            PayloadOffset = payloadOffset,
            PayloadLength = payloadLength,
            HeaderOffset = headerPos,
        });
    }

    private static uint ReadUInt32LEAt(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static uint ByteSwap(uint value) =>
        ((value & 0x000000FF) << 24) |
        ((value & 0x0000FF00) << 8) |
        ((value & 0x00FF0000) >> 8) |
        ((value & 0xFF000000) >> 24);
}
