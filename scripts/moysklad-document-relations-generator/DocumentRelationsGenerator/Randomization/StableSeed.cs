using System.Text;

namespace DocumentRelationsGenerator.Randomization;

/// <summary>
/// Derives independent, process-stable seeds (string.GetHashCode is randomized per process, so it is
/// not used). Filtering scenarios therefore does not change the values of the remaining scenarios.
/// </summary>
public static class StableSeed
{
    public static int Derive(int seed, params object[] parts)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(seed + "|" + string.Join("|", parts)))
        {
            hash ^= b;
            hash *= prime;
        }

        return (int)(hash ^ (hash >> 32)) & int.MaxValue;
    }
}
