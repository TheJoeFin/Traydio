using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services;

namespace Trdo.ViewModels;

/// <summary>
/// A named codec choice for the codec filter dropdown.
/// An empty <see cref="Value"/> means "any".
/// </summary>
public record CodecOption(string Display, string Value);

/// <summary>
/// A minimum-bitrate choice for the bitrate filter dropdown.
/// A <see cref="Value"/> of 0 means "any".
/// </summary>
public record BitrateOption(string Display, int Value);

/// <summary>
/// A sort choice mapping to the Radio Browser API's order/reverse parameters.
/// </summary>
public record SortOption(string Display, string Order, bool Reverse);

public partial class SearchStationViewModel : INotifyPropertyChanged
{
    private readonly RadioBrowserService _radioBrowserService = new();
    private CancellationTokenSource? _searchCancellationTokenSource;
    private bool _filterOptionsLoaded;

    private string _searchTerm = string.Empty;
    private bool _isSearching;
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private bool _isLoadingFilterOptions;

    private CodecOption? _selectedCodec;
    private BitrateOption? _selectedBitrate;
    private bool _hideBroken;
    private SortOption _selectedSort;

    /// <summary>
    /// Every country, language and genre the directory knows, flattened into one searchable
    /// list. Built once when the filter panel first opens.
    /// </summary>
    private readonly List<StationFilterOption> _allFilterOptions = [];

    private string _filterQuery = string.Empty;
    private bool _isFilterPanelOpen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SearchStationViewModel()
    {
        _selectedSort = SortOptions[0];
    }

    // Results and filter option sources ---------------------------------

    /// <summary>
    /// Search results grouped by station name - see <see cref="StationSearchGroupingPolicy"/>.
    /// Mirrors of the same station (a relay, a backup feed, a second codec) collapse into one
    /// card with several streams rather than several near-identical rows.
    /// </summary>
    public ObservableCollection<StationSearchResultGroup> SearchResultGroups { get; } = [];

    /// <summary>The country/language/genre filters currently narrowing the search.</summary>
    public ObservableCollection<StationFilterOption> ActiveFilters { get; } = [];

    /// <summary>
    /// Every applied filter as a removable chip — <see cref="ActiveFilters"/> plus a chip for
    /// each non-default codec/bitrate/sort/hide-broken pick. What the page's chip row binds to.
    /// </summary>
    public ObservableCollection<IStationFilterChip> FilterChips { get; } = [];

    /// <summary>What the filter picker is offering for the text typed into it.</summary>
    public ObservableCollection<StationFilterOption> FilterSuggestions { get; } = [];

    public IReadOnlyList<CodecOption> Codecs { get; } =
    [
        new("Any codec", string.Empty),
        new("MP3", "MP3"),
        new("AAC", "AAC"),
        new("AAC+", "AAC+"),
        new("OGG", "OGG"),
        new("FLAC", "FLAC"),
    ];

    public IReadOnlyList<BitrateOption> Bitrates { get; } =
    [
        new("Any bitrate", 0),
        new("64 kbps+", 64),
        new("128 kbps+", 128),
        new("192 kbps+", 192),
        new("256 kbps+", 256),
        new("320 kbps+", 320),
    ];

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("Most voted", "votes", true),
        new("Most popular", "clickcount", true),
        new("Name (A–Z)", "name", false),
        new("Random", "random", false),
    ];

    // Search box and status ---------------------------------------------

    public string SearchTerm
    {
        get => _searchTerm;
        set
        {
            if (value == _searchTerm) return;
            _searchTerm = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowInitialState));
            _ = PerformSearchAsync();
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (value == _isSearching) return;
            _isSearching = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowInitialState));
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (value == _hasError) return;
            _hasError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowInitialState));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (value == _errorMessage) return;
            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsLoadingFilterOptions
    {
        get => _isLoadingFilterOptions;
        private set
        {
            if (value == _isLoadingFilterOptions) return;
            _isLoadingFilterOptions = value;
            OnPropertyChanged();
        }
    }

    // Filter picker ------------------------------------------------------

    /// <summary>
    /// Whether the filter panel is showing in place of the results. It takes over the results
    /// area rather than opening as a flyout: in a 320px window a flyout leaves a list of
    /// hundreds of countries scrolling through a keyhole.
    /// </summary>
    public bool IsFilterPanelOpen
    {
        get => _isFilterPanelOpen;
        set
        {
            if (value == _isFilterPanelOpen) return;
            _isFilterPanelOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AreResultsVisible));
        }
    }

    /// <summary>
    /// Text typed into the filter picker. Narrows the suggestion list only — it never reaches
    /// the directory, so it costs nothing and updates on every keystroke.
    /// </summary>
    public string FilterQuery
    {
        get => _filterQuery;
        set
        {
            if (value == _filterQuery) return;
            _filterQuery = value;
            OnPropertyChanged();
            RefreshSuggestions();
        }
    }

    /// <summary>True once the picker has text but nothing matches it.</summary>
    public bool HasNoSuggestions => FilterSuggestions.Count == 0 && _allFilterOptions.Count > 0;

    /// <summary>
    /// Adds a filter, replacing any existing value of the same facet when the directory only
    /// accepts one, then reruns the search.
    /// </summary>
    public void AddFilter(StationFilterOption option)
    {
        IReadOnlyList<StationFilterOption> updated = StationFilterSearchPolicy.Apply(ActiveFilters, option);

        ActiveFilters.Clear();
        foreach (StationFilterOption filter in updated)
        {
            ActiveFilters.Add(filter);
        }

        // Clearing the box readies the picker for the next filter and re-shows the browse list,
        // which is what someone stacking up two or three filters wants next.
        FilterQuery = string.Empty;
        RefreshSuggestions();
        OnFiltersChanged();
    }

    public void RemoveFilter(StationFilterOption option)
    {
        if (!ActiveFilters.Remove(option))
            return;

        RefreshSuggestions();
        OnFiltersChanged();
    }

    /// <summary>
    /// Removes whatever chip was clicked, regardless of which side of the panel it came from.
    /// </summary>
    public void RemoveChip(IStationFilterChip chip)
    {
        switch (chip)
        {
            case StationFilterOption option:
                RemoveFilter(option);
                break;
            case QualityFilterChip { Kind: QualityChipKind.Codec }:
                SelectedCodec = null;
                break;
            case QualityFilterChip { Kind: QualityChipKind.Bitrate }:
                SelectedBitrate = null;
                break;
            case QualityFilterChip { Kind: QualityChipKind.Sort }:
                SelectedSort = SortOptions[0];
                break;
            case QualityFilterChip { Kind: QualityChipKind.HideBroken }:
                HideBroken = false;
                break;
        }
    }

    // Bounded filter selections (each change re-runs the search) ---------

    public CodecOption? SelectedCodec
    {
        get => _selectedCodec;
        set => SetFilter(ref _selectedCodec, value);
    }

    public BitrateOption? SelectedBitrate
    {
        get => _selectedBitrate;
        set => SetFilter(ref _selectedBitrate, value);
    }

    public bool HideBroken
    {
        get => _hideBroken;
        set => SetFilter(ref _hideBroken, value);
    }

    public SortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            // The Segmented control's SelectedItem can momentarily go null when its
            // SelectionMode is applied after a selection is already bound; there's always a
            // default sort, so null means "back to that" rather than "no sort".
            value ??= SortOptions[0];

            if (Equals(value, _selectedSort)) return;
            _selectedSort = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveFilters));
            RefreshFilterChips();
            _ = PerformSearchAsync();
        }
    }

    // Computed state -----------------------------------------------------

    public bool HasActiveFilters =>
        ActiveFilters.Count > 0 ||
        !string.IsNullOrEmpty(SelectedCodec?.Value) ||
        (SelectedBitrate?.Value ?? 0) > 0 ||
        HideBroken ||
        !Equals(SelectedSort, SortOptions[0]);

    public bool ShowInitialState => string.IsNullOrWhiteSpace(SearchTerm) &&
                !HasActiveFilters &&
                SearchResultGroups.Count == 0 &&
                !IsSearching &&
                !HasError;

    /// <summary>Results give up the screen while the filter panel is open.</summary>
    public bool AreResultsVisible => !IsFilterPanelOpen;

    /// <summary>
    /// Loads every country, language and genre the first time the filter panel opens, and
    /// flattens them into the one list the picker searches.
    /// </summary>
    public async Task LoadFilterOptionsAsync()
    {
        if (_filterOptionsLoaded || IsLoadingFilterOptions)
        {
            RefreshSuggestions();
            return;
        }

        IsLoadingFilterOptions = true;
        try
        {
            List<RadioBrowserCountry> countries = await _radioBrowserService.GetCountriesAsync();
            List<RadioBrowserLanguage> languages = await _radioBrowserService.GetLanguagesAsync();
            List<RadioBrowserTag> tags = await _radioBrowserService.GetTagsAsync();

            _allFilterOptions.Clear();

            foreach (RadioBrowserCountry country in countries)
            {
                _allFilterOptions.Add(
                    new StationFilterOption(StationFilterFacet.Country, country.Name, country.StationCount));
            }

            foreach (RadioBrowserLanguage language in languages)
            {
                _allFilterOptions.Add(
                    new StationFilterOption(StationFilterFacet.Language, language.Name, language.StationCount));
            }

            foreach (RadioBrowserTag tag in tags)
            {
                _allFilterOptions.Add(
                    new StationFilterOption(StationFilterFacet.Genre, tag.Name, tag.StationCount));
            }

            _filterOptionsLoaded = true;
        }
        catch (Exception ex)
        {
            // Non-fatal: the panel still works with free-text search; just log it.
            Debug.WriteLine($"[SearchStationViewModel] Failed to load filter options: {ex.Message}");
        }
        finally
        {
            IsLoadingFilterOptions = false;
            RefreshSuggestions();
        }
    }

    /// <summary>
    /// Re-ranks the picker's suggestions against what has been typed and what is already
    /// applied. Cheap enough to run on every keystroke: it is a scan of a few hundred strings
    /// held in memory, with no network involved.
    /// </summary>
    private void RefreshSuggestions()
    {
        IReadOnlyList<StationFilterOption> suggestions =
            StationFilterSearchPolicy.Suggest(_allFilterOptions, FilterQuery, ActiveFilters);

        FilterSuggestions.Clear();
        foreach (StationFilterOption option in suggestions)
        {
            FilterSuggestions.Add(option);
        }

        OnPropertyChanged(nameof(HasNoSuggestions));
    }

    /// <summary>
    /// Clears every filter. Each reset kicks off a search, but PerformSearchAsync debounces and
    /// cancels the prior run, so the net effect is a single refreshed search.
    /// </summary>
    public void ClearFilters()
    {
        SelectedCodec = null;
        SelectedBitrate = null;
        HideBroken = false;
        SelectedSort = SortOptions[0];

        if (ActiveFilters.Count == 0)
            return;

        ActiveFilters.Clear();
        RefreshSuggestions();
        OnFiltersChanged();
    }

    /// <summary>
    /// Shared tail of every chip change: republish the derived state the page binds to, then
    /// research.
    /// </summary>
    private void OnFiltersChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ShowInitialState));
        RefreshFilterChips();
        _ = PerformSearchAsync();
    }

    /// <summary>
    /// Rebuilds the combined chip row from <see cref="ActiveFilters"/> plus whichever
    /// codec/bitrate/sort/hide-broken picks are away from their "any" default. Cheap enough to
    /// rebuild from scratch on every change: at most a handful of chips.
    /// </summary>
    private void RefreshFilterChips()
    {
        FilterChips.Clear();

        foreach (StationFilterOption option in ActiveFilters)
        {
            FilterChips.Add(option);
        }

        if (!string.IsNullOrEmpty(SelectedCodec?.Value))
            FilterChips.Add(new QualityFilterChip(QualityChipKind.Codec, $"Codec: {SelectedCodec!.Display}"));

        if ((SelectedBitrate?.Value ?? 0) > 0)
            FilterChips.Add(new QualityFilterChip(QualityChipKind.Bitrate, $"Bitrate: {SelectedBitrate!.Display}"));

        if (!Equals(SelectedSort, SortOptions[0]))
            FilterChips.Add(new QualityFilterChip(QualityChipKind.Sort, $"Sort: {SelectedSort.Display}"));

        if (HideBroken)
            FilterChips.Add(new QualityFilterChip(QualityChipKind.HideBroken, "Hide broken stations"));
    }

    private async Task PerformSearchAsync()
    {
        // Cancel any ongoing search
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource = new CancellationTokenSource();

        // Wait a bit for the user to finish typing / adjusting filters
        try
        {
            await Task.Delay(500, _searchCancellationTokenSource.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        StationSearchQuery query = BuildQuery();

        // Nothing to search for: clear results and return to the initial state.
        if (query.IsEmpty)
        {
            SearchResultGroups.Clear();
            HasError = false;
            ErrorMessage = string.Empty;
            OnPropertyChanged(nameof(ShowInitialState));
            return;
        }

        IsSearching = true;
        HasError = false;
        ErrorMessage = string.Empty;

        try
        {
            List<RadioBrowserStation> results = await _radioBrowserService.SearchAsync(
                query,
                _searchCancellationTokenSource.Token);

            SearchResultGroups.Clear();
            foreach (StationSearchResultGroup group in StationSearchGroupingPolicy.Group(results))
            {
                SearchResultGroups.Add(group);
            }

            OnPropertyChanged(nameof(ShowInitialState));
            Debug.WriteLine($"[SearchStationViewModel] Search completed. Found {results.Count} stations in {SearchResultGroups.Count} groups");
        }
        catch (TaskCanceledException)
        {
            // Search was cancelled, ignore
            Debug.WriteLine("[SearchStationViewModel] Search cancelled");
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[SearchStationViewModel] Network error: {ex.Message}");
            HasError = true;
            ErrorMessage = ex.StatusCode.HasValue
                ? $"Server error ({(int)ex.StatusCode}): The radio station service is temporarily unavailable. Please try again later."
                : "Network error: Unable to reach the radio station service. Please check your internet connection.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SearchStationViewModel] Search error: {ex.Message}");
            HasError = true;
            ErrorMessage = $"An error occurred while searching: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private StationSearchQuery BuildQuery()
    {
        // Country and language are single-valued in the directory's API, so at most one chip of
        // each can be in play; genres are sent as a list and narrow each other.
        string[] genres = ActiveFilters
            .Where(filter => filter.Facet == StationFilterFacet.Genre)
            .Select(filter => filter.Value)
            .ToArray();

        return new StationSearchQuery
        {
            Name = SearchTerm,
            Country = FirstValue(StationFilterFacet.Country),
            Language = FirstValue(StationFilterFacet.Language),
            Tags = genres.Length > 0 ? genres : null,
            Codec = string.IsNullOrEmpty(SelectedCodec?.Value) ? null : SelectedCodec!.Value,
            BitrateMin = (SelectedBitrate?.Value ?? 0) > 0 ? SelectedBitrate!.Value : null,
            Order = SelectedSort.Order,
            Reverse = SelectedSort.Reverse,
            HideBroken = HideBroken,
            Limit = 50
        };
    }

    private string? FirstValue(StationFilterFacet facet) =>
        ActiveFilters.FirstOrDefault(filter => filter.Facet == facet)?.Value;

    private void SetFilter<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ShowInitialState));
        RefreshFilterChips();
        _ = PerformSearchAsync();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
