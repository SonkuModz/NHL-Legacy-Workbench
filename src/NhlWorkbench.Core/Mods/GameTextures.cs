using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;

namespace NhlWorkbench.Core.Mods;

/// <summary>One texture's pixels, whichever kind of game file it came from.</summary>
public sealed record GameTexture(int Width, int Height, TextureFormat Format, int MipCount, byte[] LinearPixelData);

/// <summary>Reads and changes textures the same way in 3D art (.rpsgl) and menu art (.big).</summary>
public static class GameTextures
{
    public static Result<GameTexture> Read(byte[] file, string gamePath, int textureIndex, DdsCodec codec)
    {
        if (MenuArt.IsPackage(gamePath))
        {
            Result<DecodedDdsTexture> menu = MenuArt.Read(file, gamePath, textureIndex, codec);
            return menu.IsSuccess
                ? Result.Success(new GameTexture(menu.Value!.Width, menu.Value.Height, menu.Value.Format, menu.Value.MipCount, menu.Value.LinearData))
                : Result.Failure<GameTexture>(menu.Error!, menu.Exception);
        }

        Result<RpsglContainer> parsed = RpsglReader.Parse(file, gamePath);
        if (!parsed.IsSuccess)
        {
            return Result.Failure<GameTexture>(parsed.Error!, parsed.Exception);
        }

        TextureEntry? texture = PictureSwap.FindTexture(parsed.Value!, textureIndex);
        return texture is null
            ? Result.Failure<GameTexture>($"{gamePath} has no texture number {textureIndex + 1}.")
            : Result.Success(new GameTexture(texture.Width, texture.Height, texture.Format, texture.MipCount, texture.LinearPixelData));
    }

    public static Result<byte[]> Replace(byte[] file, string gamePath, Rgba32Image picture, DdsCodec codec, int textureIndex) =>
        MenuArt.IsPackage(gamePath)
            ? MenuArt.Replace(file, gamePath, picture, codec, textureIndex)
            : PictureSwap.Replace(file, gamePath, picture, codec, textureIndex);

    public static Result<byte[]> Import(byte[] file, string gamePath, byte[] dds, DdsCodec codec, int textureIndex) =>
        MenuArt.IsPackage(gamePath)
            ? MenuArt.Import(file, gamePath, dds, codec, textureIndex)
            : TextureDds.Import(file, gamePath, dds, codec, textureIndex);

    public static Result<ExportedDds> Export(byte[] file, string gamePath, DdsCodec codec, int textureIndex) =>
        MenuArt.IsPackage(gamePath)
            ? MenuArt.Export(file, gamePath, textureIndex)
            : TextureDds.ExportMain(file, gamePath, codec, textureIndex);
}
