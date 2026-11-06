namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>A parsed ABKC/S10A audio bank: the raw file bytes plus its per-sound table.</summary>
public sealed class AbkFile
{
    public AbkFile(byte[] data, long bankStart, IReadOnlyList<AbkSound> sounds)
    {
        Data = data;
        BankStart = bankStart;
        Sounds = sounds;
    }

    public byte[] Data { get; }

    /// <summary>Absolute offset of the "S10A" magic.</summary>
    public long BankStart { get; }

    public IReadOnlyList<AbkSound> Sounds { get; }

    public byte[] GetPayload(AbkSound sound)
    {
        var result = new byte[sound.PayloadLength];
        Array.Copy(Data, sound.PayloadOffset, result, 0, sound.PayloadLength);
        return result;
    }
}
