namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// One entry from an Apt movie header's class/import table -- either the movie's own class
/// identity (always index 0) or one imported component class it depends on. <see
/// cref="Aux1"/>/<see cref="Aux2"/> are the 3rd/4th u32 of the 4-tuple this was read from;
/// cross-checked against three real movies (see <see cref="AptMovieReader"/> remarks) and
/// confirmed to NOT be string-pool offsets like the first two fields are -- their actual
/// meaning is unconfirmed, so they are surfaced as raw numbers for the developer panel rather
/// than given a guessed name.
/// </summary>
public sealed record AptClassReference(string FullPath, string ShortName, uint Aux1, uint Aux2);
