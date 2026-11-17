using System;

namespace IGYO
{
    // The tools: what acts on the page on show. They live in the tools menu (the button at the right of the
    // toolbar, and "Tools" in the main menu), and each of them can be pinned to the toolbar as a button of its own.
    static class Tools
    {
        public const string AdBlock = "adblock", Dark = "dark", Enhance = "enhance", Picker = "picker",
            Translate = "translate", Volume = "volume", Pip = "pip", Shot = "shot", Extensions = "extensions";

        // In the order the tools menu lists them.
        public static readonly string[] All = { AdBlock, Dark, Enhance, Picker, Translate, Volume, Pip, Shot, Extensions };

        public static bool IsTool(string id) => Array.IndexOf(All, id) >= 0;

        // An extension is pinned to the toolbar like a tool: as "ext:" and its name in the extensions folder (for one
        // that came from a store, its id there). Its button shows the extension's own icon.
        public const string ExtensionPrefix = "ext:";

        public static string ForExtension(string key) => ExtensionPrefix + key;

        public static bool IsExtension(string id) =>
            id != null && id.Length > ExtensionPrefix.Length && id.Length <= 200 && id.StartsWith(ExtensionPrefix, StringComparison.Ordinal) &&
            id.IndexOfAny(new[] { '/', '\\', '"', '<', '>', '|', '\n', '\r', '\t' }) < 0;

        public static string ExtensionKey(string id) => IsExtension(id) ? id.Substring(ExtensionPrefix.Length) : null;

        // What can stand on the toolbar as a button of its own.
        public static bool IsPinnable(string id) => IsTool(id) || IsExtension(id);

        // ----- video enhancement -----
        // The engine can sharpen a video while it draws it ("contrast adaptive sharpening": edges get more
        // contrast, flat areas are left alone). It is asked for per <video>, by a property the engine gives the
        // element, with a strength from 0 to 1. A video set to it is drawn by the engine itself instead of being
        // handed to the graphics card's video path, so it switches the moment the property is set.
        public static readonly string[] Levels = { "soft", "medium", "strong" };

        public static bool IsLevel(string level) => Array.IndexOf(Levels, level) >= 0;

        // What a <video>'s msVideoProcessing is set to for a level. ("cas" alone is the lowest strength; the engine
        // reads it back as "cas:0", and takes "cas:0" itself for no sharpening at all.)
        public static string EnhanceKind(string level) => level == "soft" ? "cas" : level == "medium" ? "cas:0.5" : "cas:1";
    }
}
