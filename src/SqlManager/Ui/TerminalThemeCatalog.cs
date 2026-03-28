using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using GuiAttribute = Terminal.Gui.Drawing.Attribute;
using GuiColor = Terminal.Gui.Drawing.Color;

namespace SqlManager;

internal enum TerminalThemeSurface
{
    Runnable,
    Menu,
    Dialog,
    Error
}

internal sealed record TerminalThemePalette
{
    public required string Name { get; init; }
    public required string Black { get; init; }
    public required string Red { get; init; }
    public required string Green { get; init; }
    public required string Yellow { get; init; }
    public required string Blue { get; init; }
    public required string Purple { get; init; }
    public required string Cyan { get; init; }
    public required string White { get; init; }
    public required string BrightBlack { get; init; }
    public required string BrightRed { get; init; }
    public required string BrightGreen { get; init; }
    public required string BrightYellow { get; init; }
    public required string BrightBlue { get; init; }
    public required string BrightPurple { get; init; }
    public required string BrightCyan { get; init; }
    public required string BrightWhite { get; init; }
    public required string Background { get; init; }
    public required string Foreground { get; init; }
    public required string SelectionBackground { get; init; }
    public required string CursorColor { get; init; }
}

internal static class TerminalThemeCatalog
{
    public const string DefaultThemeName = "iTerm2 Tango Dark";

    private static readonly IReadOnlyDictionary<string, TerminalThemePalette> Palettes =
        new Dictionary<string, TerminalThemePalette>(StringComparer.OrdinalIgnoreCase)
        {
            ["C64"] = new TerminalThemePalette
            {
                Name = "C64",
                Black = "#090300",
                Red = "#883932",
                Green = "#55a049",
                Yellow = "#bfce72",
                Blue = "#40318d",
                Purple = "#8b3f96",
                Cyan = "#67b6bd",
                White = "#ffffff",
                BrightBlack = "#000000",
                BrightRed = "#883932",
                BrightGreen = "#55a049",
                BrightYellow = "#bfce72",
                BrightBlue = "#40318d",
                BrightPurple = "#8b3f96",
                BrightCyan = "#67b6bd",
                BrightWhite = "#f7f7f7",
                Background = "#40318d",
                Foreground = "#7869c4",
                SelectionBackground = "#7869c4",
                CursorColor = "#7869c4"
            },
            ["CGA"] = new TerminalThemePalette
            {
                Name = "CGA",
                Black = "#000000",
                Red = "#aa0000",
                Green = "#00aa00",
                Yellow = "#aa5500",
                Blue = "#0000aa",
                Purple = "#aa00aa",
                Cyan = "#00aaaa",
                White = "#aaaaaa",
                BrightBlack = "#555555",
                BrightRed = "#ff5555",
                BrightGreen = "#55ff55",
                BrightYellow = "#ffff55",
                BrightBlue = "#5555ff",
                BrightPurple = "#ff55ff",
                BrightCyan = "#55ffff",
                BrightWhite = "#ffffff",
                Background = "#000000",
                Foreground = "#aaaaaa",
                SelectionBackground = "#c1deff",
                CursorColor = "#b8b8b8"
            },
            ["coolnight"] = new TerminalThemePalette
            {
                Name = "coolnight",
                Black = "#0B3B61",
                Red = "#FF3A3A",
                Green = "#52FFD0",
                Yellow = "#FFF383",
                Blue = "#1376F9",
                Purple = "#C792EA",
                Cyan = "#FF5ED4",
                White = "#16FDA2",
                BrightBlack = "#63686D",
                BrightRed = "#FF54B0",
                BrightGreen = "#74FFD8",
                BrightYellow = "#FCF5AE",
                BrightBlue = "#388EFF",
                BrightPurple = "#AE81FF",
                BrightCyan = "#FF6AD7",
                BrightWhite = "#60FBBF",
                Background = "#010C18",
                Foreground = "#ECDEF4",
                SelectionBackground = "#38FF9C",
                CursorColor = "#38FF9D"
            },
            ["iTerm2 Dark Background"] = new TerminalThemePalette
            {
                Name = "iTerm2 Dark Background",
                Black = "#000000",
                Red = "#c91b00",
                Green = "#00c200",
                Yellow = "#c7c400",
                Blue = "#0225c7",
                Purple = "#ca30c7",
                Cyan = "#00c5c7",
                White = "#c7c7c7",
                BrightBlack = "#686868",
                BrightRed = "#ff6e67",
                BrightGreen = "#5ffa68",
                BrightYellow = "#fffc67",
                BrightBlue = "#6871ff",
                BrightPurple = "#ff77ff",
                BrightCyan = "#60fdff",
                BrightWhite = "#ffffff",
                Background = "#000000",
                Foreground = "#c7c7c7",
                SelectionBackground = "#c1deff",
                CursorColor = "#c7c7c7"
            },
            ["iTerm2 Default"] = new TerminalThemePalette
            {
                Name = "iTerm2 Default",
                Black = "#000000",
                Red = "#c91b00",
                Green = "#00c200",
                Yellow = "#c7c400",
                Blue = "#2225c4",
                Purple = "#ca30c7",
                Cyan = "#00c5c7",
                White = "#ffffff",
                BrightBlack = "#686868",
                BrightRed = "#ff6e67",
                BrightGreen = "#5ffa68",
                BrightYellow = "#fffc67",
                BrightBlue = "#6871ff",
                BrightPurple = "#ff77ff",
                BrightCyan = "#60fdff",
                BrightWhite = "#ffffff",
                Background = "#000000",
                Foreground = "#ffffff",
                SelectionBackground = "#c1deff",
                CursorColor = "#e5e5e5"
            },
            ["iTerm2 Tango Dark"] = new TerminalThemePalette
            {
                Name = "iTerm2 Tango Dark",
                Black = "#000000",
                Red = "#d81e00",
                Green = "#5ea702",
                Yellow = "#cfae00",
                Blue = "#427ab3",
                Purple = "#89658e",
                Cyan = "#00a7aa",
                White = "#dbded8",
                BrightBlack = "#686a66",
                BrightRed = "#f54235",
                BrightGreen = "#99e343",
                BrightYellow = "#fdeb61",
                BrightBlue = "#84b0d8",
                BrightPurple = "#bc94b7",
                BrightCyan = "#37e6e8",
                BrightWhite = "#f1f1f0",
                Background = "#000000",
                Foreground = "#ffffff",
                SelectionBackground = "#c1deff",
                CursorColor = "#ffffff"
            },
            ["Monokai Vivid"] = new TerminalThemePalette
            {
                Name = "Monokai Vivid",
                Black = "#121212",
                Red = "#fa2934",
                Green = "#98e123",
                Yellow = "#fff30a",
                Blue = "#0443ff",
                Purple = "#f800f8",
                Cyan = "#01b6ed",
                White = "#ffffff",
                BrightBlack = "#838383",
                BrightRed = "#f6669d",
                BrightGreen = "#b1e05f",
                BrightYellow = "#fff26d",
                BrightBlue = "#0443ff",
                BrightPurple = "#f200f6",
                BrightCyan = "#51ceff",
                BrightWhite = "#ffffff",
                Background = "#121212",
                Foreground = "#f9f9f9",
                SelectionBackground = "#ffffff",
                CursorColor = "#fb0007"
            },
            ["Obsidian"] = new TerminalThemePalette
            {
                Name = "Obsidian",
                Black = "#000000",
                Red = "#a60001",
                Green = "#00bb00",
                Yellow = "#fecd22",
                Blue = "#3a9bdb",
                Purple = "#bb00bb",
                Cyan = "#00bbbb",
                White = "#bbbbbb",
                BrightBlack = "#555555",
                BrightRed = "#ff0003",
                BrightGreen = "#93c863",
                BrightYellow = "#fef874",
                BrightBlue = "#a1d7ff",
                BrightPurple = "#ff55ff",
                BrightCyan = "#55ffff",
                BrightWhite = "#ffffff",
                Background = "#283033",
                Foreground = "#cdcdcd",
                SelectionBackground = "#3e4c4f",
                CursorColor = "#c0cad0"
            },
            ["Solarized Dark Higher Contrast"] = new TerminalThemePalette
            {
                Name = "Solarized Dark Higher Contrast",
                Black = "#002831",
                Red = "#d11c24",
                Green = "#6cbe6c",
                Yellow = "#a57706",
                Blue = "#2176c7",
                Purple = "#c61c6f",
                Cyan = "#259286",
                White = "#eae3cb",
                BrightBlack = "#006488",
                BrightRed = "#f5163b",
                BrightGreen = "#51ef84",
                BrightYellow = "#b27e28",
                BrightBlue = "#178ec8",
                BrightPurple = "#e24d8e",
                BrightCyan = "#00b39e",
                BrightWhite = "#fcf4dc",
                Background = "#001e27",
                Foreground = "#9cc2c3",
                SelectionBackground = "#003748",
                CursorColor = "#f34b00"
            },
            ["Adventure"] = new TerminalThemePalette
            {
                Name = "Adventure",
                Black = "#040404",
                Red = "#d84a33",
                Green = "#5da602",
                Yellow = "#eebb6e",
                Blue = "#417ab3",
                Purple = "#e5c499",
                Cyan = "#bdcfe5",
                White = "#dbded8",
                BrightBlack = "#685656",
                BrightRed = "#d76b42",
                BrightGreen = "#99b52c",
                BrightYellow = "#ffb670",
                BrightBlue = "#97d7ef",
                BrightPurple = "#aa7900",
                BrightCyan = "#bdcfe5",
                BrightWhite = "#e4d5c7",
                Background = "#040404",
                Foreground = "#feffff",
                SelectionBackground = "#606060",
                CursorColor = "#feffff"
            }
        };

    public static IReadOnlyList<string> GetThemeNames()
        => Palettes.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

    public static string NormalizeThemeName(string? themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName))
        {
            return DefaultThemeName;
        }

        return Palettes.TryGetValue(themeName, out var palette)
            ? palette.Name
            : DefaultThemeName;
    }

    public static string? ResolveThemeName(ColorCapabilityLevel capability)
        => capability == ColorCapabilityLevel.NoColor ? null : DefaultThemeName;

    public static bool TryCreateSchemes(ColorCapabilityLevel capability, string? preferredThemeName, out Dictionary<TerminalThemeSurface, Scheme> schemes)
    {
        schemes = [];

        var themeName = capability == ColorCapabilityLevel.NoColor
            ? null
            : NormalizeThemeName(preferredThemeName);
        if (themeName is null || !Palettes.TryGetValue(themeName, out var palette))
        {
            return false;
        }

        schemes[TerminalThemeSurface.Runnable] = CreateRunnableScheme(palette);
        schemes[TerminalThemeSurface.Menu] = CreateMenuScheme(palette);
        schemes[TerminalThemeSurface.Dialog] = CreateDialogScheme(palette);
        schemes[TerminalThemeSurface.Error] = CreateErrorScheme(palette);
        return true;
    }

    private static Scheme CreateRunnableScheme(TerminalThemePalette palette)
        => new()
        {
            Normal = CreateAttribute(palette.Foreground, palette.Background),
            HotNormal = CreateAttribute(palette.BrightYellow, palette.Background),
            Focus = CreateAttribute(palette.Background, palette.SelectionBackground),
            HotFocus = CreateAttribute(palette.Background, palette.BrightYellow),
            Active = CreateAttribute(palette.BrightWhite, palette.Background),
            HotActive = CreateAttribute(palette.BrightYellow, palette.Background),
            Highlight = CreateAttribute(palette.Background, palette.SelectionBackground),
            Editable = CreateAttribute(palette.Foreground, palette.Background),
            ReadOnly = CreateAttribute(palette.White, palette.Background),
            Disabled = CreateAttribute(palette.BrightBlack, palette.Background)
        };

    private static Scheme CreateMenuScheme(TerminalThemePalette palette)
        => new()
        {
            Normal = CreateAttribute(palette.Background, palette.SelectionBackground),
            HotNormal = CreateAttribute(palette.Yellow, palette.SelectionBackground),
            Focus = CreateAttribute(palette.Background, palette.BrightBlue),
            HotFocus = CreateAttribute(palette.Background, palette.BrightYellow),
            Active = CreateAttribute(palette.Foreground, palette.Background),
            HotActive = CreateAttribute(palette.BrightYellow, palette.Background),
            Highlight = CreateAttribute(palette.Background, palette.BrightBlue),
            Editable = CreateAttribute(palette.Foreground, palette.Background),
            ReadOnly = CreateAttribute(palette.White, palette.Background),
            Disabled = CreateAttribute(palette.BrightBlack, palette.Background)
        };

    private static Scheme CreateDialogScheme(TerminalThemePalette palette)
        => new()
        {
            Normal = CreateAttribute(palette.Foreground, palette.Background),
            HotNormal = CreateAttribute(palette.BrightYellow, palette.Background),
            Focus = CreateAttribute(palette.Background, palette.SelectionBackground),
            HotFocus = CreateAttribute(palette.Background, palette.BrightYellow),
            Active = CreateAttribute(palette.BrightCyan, palette.Background),
            HotActive = CreateAttribute(palette.BrightYellow, palette.Background),
            Highlight = CreateAttribute(palette.Background, palette.SelectionBackground),
            Editable = CreateAttribute(palette.Foreground, palette.Background),
            ReadOnly = CreateAttribute(palette.White, palette.Background),
            Disabled = CreateAttribute(palette.BrightBlack, palette.Background)
        };

    private static Scheme CreateErrorScheme(TerminalThemePalette palette)
        => new()
        {
            Normal = CreateAttribute(palette.BrightWhite, palette.Red),
            HotNormal = CreateAttribute(palette.BrightYellow, palette.Red),
            Focus = CreateAttribute(palette.Background, palette.BrightRed),
            HotFocus = CreateAttribute(palette.Background, palette.BrightYellow),
            Active = CreateAttribute(palette.BrightWhite, palette.Red),
            HotActive = CreateAttribute(palette.BrightYellow, palette.Red),
            Highlight = CreateAttribute(palette.Background, palette.BrightRed),
            Editable = CreateAttribute(palette.BrightWhite, palette.Red),
            ReadOnly = CreateAttribute(palette.BrightWhite, palette.Red),
            Disabled = CreateAttribute(palette.BrightBlack, palette.Red)
        };

    private static GuiAttribute CreateAttribute(string foreground, string background)
        => new(new GuiColor(foreground), new GuiColor(background));
}
