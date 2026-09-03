using TLOverlay.App.Interop;
using TLOverlay.Core.Input;

namespace TLOverlay.App.Services;

/// <summary>
/// The player's hotkeys: the defaults, with whatever they changed on top.
///
/// Stored as an overlay rather than as the whole set, so an action added in a
/// later version arrives with a working default instead of no key at all - and a
/// player who rebound one key does not silently freeze the other five at the
/// shape they had the day they changed it.
/// </summary>
public static class HotKeyProfile
{
    /// <summary>
    /// The bindings to register, in the order the defaults declare them.
    ///
    /// Anything unusable in the settings file - a key name that is not a key, a
    /// combination with no modifier, a duplicate - falls back to the default for
    /// that action. A binding that cannot be registered is worse than the default
    /// one, because the player is left with no key rather than the wrong key.
    /// </summary>
    public static IReadOnlyList<HotKeyBinding> Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var bindings = new List<HotKeyBinding>(GlobalHotKeyService.Defaults.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HotKeyBinding fallback in GlobalHotKeyService.Defaults)
        {
            HotKeyBinding binding = Resolve(settings, fallback);

            // Two actions on one combination would leave whichever registers
            // second doing nothing at all, with nothing on screen to say why.
            if (!taken.Add(binding.Gesture))
            {
                binding = fallback;
                taken.Add(fallback.Gesture);
            }

            bindings.Add(binding);
        }

        return bindings;
    }

    /// <summary>
    /// Writes the set, keeping only what differs from the defaults. Passing the
    /// defaults back therefore clears the file rather than pinning them.
    /// </summary>
    public static void Save(AppSettings settings, IEnumerable<HotKeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bindings);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (HotKeyBinding binding in bindings)
        {
            HotKeyBinding? fallback = GlobalHotKeyService.Defaults
                .FirstOrDefault(candidate => candidate.Action == binding.Action);

            if (fallback is null || !string.Equals(fallback.Gesture, binding.Gesture, StringComparison.OrdinalIgnoreCase))
            {
                overrides[binding.Action.ToString()] = binding.Gesture;
            }
        }

        settings.HotKeys = overrides;
    }

    /// <summary>Forgets every change, so the defaults apply again.</summary>
    public static void Reset(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.HotKeys = [];
    }

    private static HotKeyBinding Resolve(AppSettings settings, HotKeyBinding fallback)
    {
        if (!settings.HotKeys.TryGetValue(fallback.Action.ToString(), out string? written)
            || !HotKeyGesture.TryParse(written, out HotKeyGesture gesture))
        {
            return fallback;
        }

        return HotKeyBinding.FromGesture(fallback.Action, gesture) ?? fallback;
    }
}
