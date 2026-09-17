using Microsoft.Windows.ApplicationModel.Resources;
using System;
using Windows.Globalization;

namespace Trdo.Services;

/// <summary>
/// Resource lookups for strings that cannot be declared with <c>x:Uid</c>, such as text built in
/// code-behind, view model state, and window titles. XAML should use <c>x:Uid</c> instead so MRT
/// resolves the string while the tree loads.
/// </summary>
public static class LocalizationService
{
    public const string DefaultLanguage = "en-US";

    /// <summary>
    /// Sentinel stored/selected when the app should follow the OS-configured language instead of
    /// overriding it.
    /// </summary>
    public const string SystemLanguage = "system";

    /// <summary>
    /// UI languages that ship with a Strings\&lt;tag&gt;\Resources.resw file, in the same order as
    /// the language picker on the settings page (excluding the "System" option).
    /// i.e. also add it (in the same order) in the ComboBox in Pages\SettingsPage.xaml
    /// </summary>
    public static string[] SupportedLanguages { get; } = ["en-US", "es-ES", "fr-FR", "zh-CN", "de-DE", "pt-BR", "pt-PT", "ar-SA", "pl-PL"];

    /// <summary>
    /// All selectable options in the settings page language picker, in display order: "System"
    /// followed by each supported language tag.
    /// </summary>
    public static string[] LanguagePickerOptions { get; } = [SystemLanguage, .. SupportedLanguages];

    private static readonly ResourceLoader _loader = new();

    /// <summary>
    /// Looks up <paramref name="key"/> in Resources.resw, returning <paramref name="fallback"/>
    /// when the resource is missing so a bad key degrades to English instead of empty UI.
    /// </summary>
    public static string GetString(string key, string fallback = "")
    {
        try
        {
            string value = _loader.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Applies the saved UI language for the process. Must run before any XAML loads, because
    /// x:Uid resources are resolved once while the element tree is created.
    /// </summary>
    public static void ApplyLanguage(string languageTag)
    {
        try
        {
            // An empty override tells the platform to use the OS-configured language.
            string effectiveTag = string.IsNullOrWhiteSpace(languageTag) || languageTag == SystemLanguage
                ? string.Empty
                : languageTag;

            ApplicationLanguages.PrimaryLanguageOverride = effectiveTag;

            LogService.Info("Localization",
                string.IsNullOrEmpty(effectiveTag)
                    ? $"Applied language override: <none> (requested '{languageTag}', following OS language)"
                    : $"Applied language override: '{effectiveTag}'");
        }
        catch (Exception ex)
        {
            // Ignore invalid culture tags and fall back to the system language.
            LogService.Warn("Localization", $"Failed to apply language override '{languageTag}': {ex.Message}");
        }
    }
}
