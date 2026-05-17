/*
 * Copyright (c) 2026 Noah Dolph
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 *
 * See the GNU Affero General Public License for more details.
 */
using System;
using System.Text.RegularExpressions;

namespace EtheirysProximityVoiceChat;

public static class Utils
{
    /// <summary>
    /// Requires an input format of Firstname Lastname@Worldname. Returns the input string if format is wrong.
    /// </summary>
    public static string ConvertToInitialsName(string playerName)
    {
        var regex = @"(\w)\w*\s(\w)\w*(@\w+)";

        var match = Regex.Match(playerName, regex);
        if (match != null)
        {
            var groups = match.Groups;
            if (groups != null && groups.Count >= 4)
            {
                // S. N.@World
                return $"{groups[1].Value}. {groups[2].Value}.{groups[3].Value}";
            }
        }
        return playerName;
    }

    /// <summary>
    /// Checks if a string contains any links (http://, https://, www., etc.).
    /// </summary>
    public static bool ContainsLinks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var linkPattern = @"(https?://|www\.|ftp://|ftps://)";
        return Regex.IsMatch(text, linkPattern, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Validates that a Twitch link is in the correct format: https://twitch.tv/username
    /// </summary>
    public static bool IsValidTwitchLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
            return true; // Empty is valid (user hasn't set one)

        try
        {
            if (!link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !link.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var uri = new Uri(link);
            if (!uri.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase))
                return false;

            // Must have at least a username after twitch.tv/
            var path = uri.AbsolutePath.Trim('/');
            return !string.IsNullOrWhiteSpace(path) && !path.Contains('/');
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that a hex color string is valid (3 or 6 characters, all hex digits).
    /// </summary>
    public static bool IsValidHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        // Remove # if present
        var cleaned = hex.StartsWith('#') ? hex[1..] : hex;

        // Must be 3 or 6 characters and all hex
        return (cleaned.Length == 3 || cleaned.Length == 6) &&
               Regex.IsMatch(cleaned, @"^[0-9A-Fa-f]+$");
    }

    /// <summary>
    /// Converts a hex color string to a normalized 6-character format.
    /// E.g., "FFF" → "FFFFFF", "FF5733" → "FF5733"
    /// </summary>
    public static string NormalizeHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return "FFFFFF";

        var cleaned = hex.StartsWith('#') ? hex[1..] : hex;

        if (cleaned.Length == 3)
        {
            // Expand shorthand: "FFF" → "FFFFFF"
            return $"{cleaned[0]}{cleaned[0]}{cleaned[1]}{cleaned[1]}{cleaned[2]}{cleaned[2]}";
        }

        return cleaned.Length == 6 ? cleaned.ToUpper() : "FFFFFF";
    }
}
