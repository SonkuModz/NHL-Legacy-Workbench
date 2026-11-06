namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>How <see cref="AptPositionedElement"/> should be rendered by a preview.</summary>
public enum AptPositionedElementKind
{
    /// <summary>A Shape backed by a real bitmap texture -- render the actual image.</summary>
    TexturedShape,

    /// <summary>A Shape backed by vector geometry (no bitmap) -- no real image available; render
    /// as a labeled placeholder.</summary>
    VectorShape,

    /// <summary>A live text field (score, clock, team name, etc.) -- content is supplied by the
    /// game at runtime, never stored statically; render as a labeled placeholder.</summary>
    Text,

    /// <summary>A Sprite/Movie container. Its real visual content is its own nested children,
    /// which this workbench cannot reliably enumerate for every sprite (see
    /// <see cref="AptPositionedPreviewBuilder"/> remarks) -- render as a labeled marker only.</summary>
    Container,
}
