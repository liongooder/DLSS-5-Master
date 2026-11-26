using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

public enum NeuralModelChoice { Automatic, Community, Nvidia }

public static partial class NeuralRuntimeSelection
{
    [GeneratedRegex(@"\bGeForce\s+RTX\s+50[0-9]{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeForceRtx50();

    [GeneratedRegex(@"\b(?:NVIDIA|GeForce|Quadro|Tesla)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaAdapter();

    public static NeuralModelChoice Resolve(NeuralModelChoice preference)
        => preference is NeuralModelChoice.Community or NeuralModelChoice.Nvidia
            ? preference
            : Resolve(preference, Gpu.PresentNames);

    public static NeuralModelChoice Resolve(NeuralModelChoice preference, IEnumerable<string> adapterNames)
    {
        if (preference is NeuralModelChoice.Community or NeuralModelChoice.Nvidia) return preference;

        var hasRtx50 = false;
        foreach (var adapter in adapterNames)
        {
            if (GeForceRtx50().IsMatch(adapter))
                hasRtx50 = true;
            // The installer does not know which NVIDIA adapter a game will use.
            else if (NvidiaAdapter().IsMatch(adapter))
                return NeuralModelChoice.Community;
        }
        return hasRtx50 ? NeuralModelChoice.Nvidia : NeuralModelChoice.Community;
    }
}
