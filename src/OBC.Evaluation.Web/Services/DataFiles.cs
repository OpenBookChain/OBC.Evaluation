using OBC.Evaluation.Core;

namespace OBC.Evaluation.Web.Services;

/// <summary>Where the generated data sets live (OBC.SimulationDataGenerator/evaluation/data), for attaching CSVs and checking their hashes.</summary>
public sealed class DataFiles(string root)
{
    public string Root { get; } = root;

    public string PathOf(BundleDataset dataset, string file) => Path.Combine(Root, dataset.Folder, file);

    /// <summary>Whether the file on disk is the one the bundle was built from: true, false, or null when it's missing.</summary>
    public bool? Matches(BundleDataset dataset, string file)
    {
        var path = PathOf(dataset, file);
        return File.Exists(path) ? Hashing.Sha256(File.ReadAllBytes(path)) == dataset.Files[file] : null;
    }
}
