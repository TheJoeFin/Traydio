using Trdo.Services;
namespace Trdo.Models;

/// <summary>
/// The station attributes that have too many possible values to browse in a dropdown.
/// Codec, bitrate and sort order are deliberately absent: those are short, fixed lists, offered
/// through <see cref="QualityFilterChip"/> instead once picked.
/// </summary>
public enum StationFilterFacet
{
    Country,
    Language,
    Genre
}

/// <summary>
/// Something narrowing the search that shows up as a removable chip. Implemented by
/// <see cref="StationFilterOption"/> (country/language/genre, picked from the search-as-you-type
/// list) and <see cref="QualityFilterChip"/> (codec/bitrate/sort/hide-broken, picked from the
/// fixed controls below it) so both flow through one chip row and one removal path.
/// </summary>
public interface IStationFilterChip
{
    /// <summary>Chip text, already including which attribute this narrows.</summary>
    string ChipLabel { get; }
}

/// <summary>
/// One value a search can be narrowed by — "Country: Germany", "Genre: jazz" — together with
/// how many stations carry it.
/// <para>
/// The same type serves as a suggestion in the picker and as an applied filter chip, so
/// picking one is just a move between two lists rather than a translation.
/// </para>
/// </summary>
public sealed record StationFilterOption(StationFilterFacet Facet, string Value, int StationCount) : IStationFilterChip
{
    /// <summary>Short badge text identifying which attribute this narrows.</summary>
    public string FacetLabel => Facet switch
    {
        StationFilterFacet.Country => LocalizedText.Get("Ui_StationFilterOption_701d021d08", "Country"),
        StationFilterFacet.Language => LocalizedText.Get("Ui_StationFilterOption_a4fe65264e", "Language"),
        _ => LocalizedText.Get("Ui_StationFilterOption_6da795a866", "Genre")
    };

    /// <summary>The station count, formatted for the suggestion row's trailing column.</summary>
    public string CountLabel => StationCount == 1 ? LocalizedText.Get("Ui_StationFilterOption_85993e22a3", "1 station") : LocalizedText.Format("Ui_StationFilterOption_d088bdf306", "{0:N0} stations", StationCount);

    /// <summary>Chip text: the value alone would be ambiguous once several facets are in play.</summary>
    public string ChipLabel => $"{FacetLabel}: {Value}";
}
