namespace Catalog.Embeddings;

/// <summary>
/// The arithmetic on embedding vectors that more than one part of the catalog needs (mixes, taste matches, nanogenres),
/// so "distance" and "median" mean one thing everywhere.
/// </summary>
public static class Vectors
{
    public static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    /// <summary>
    /// Cosine distance between two unit vectors: 1 − their dot product.
    /// </summary>
    public static double Distance(float[] a, float[] b) => 1 - Dot(a, b);

    /// <summary>
    /// The middle value, or the mean of the two middle ones; 0 for none. Sorts <paramref name="values"/>.
    /// </summary>
    public static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }
}
