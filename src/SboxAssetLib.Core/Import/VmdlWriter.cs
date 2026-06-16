using System.Text;

namespace SboxAssetLib.Core.Import;

/// <summary>
/// Generates a Source 2 / s&amp;box <c>.vmdl</c> (ModelDoc) that wraps an imported mesh
/// (FBX/glTF/OBJ) via a <c>RenderMeshFile</c> node so the asset compiles to a usable model.
///
/// Materials are resolved by s&amp;box from the mesh's material names against the search path,
/// so we place a generated <c>.vmat</c> alongside the mesh.
///
/// The kv3 text-encoding and generic-format GUIDs below are stable Source 2 values and are
/// broadly accepted for .vmdl text. The in-engine plugin authors models through the editor
/// API instead (always version-correct); this template serves the standalone library path
/// and is confirmed in-engine on first open.
/// </summary>
public static class VmdlWriter
{
    public const string DefaultHeader =
        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} " +
        "format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->";

    /// <param name="name">Logical model name (the asset id).</param>
    /// <param name="meshRelPath">Addon-relative path to the mesh source, e.g. "models/nature/rock_01/rock_01.fbx".</param>
    /// <param name="importScale">Uniform import scale (Source 2 is in inches; many CC0 meshes are in metres).</param>
    /// <param name="header">Override the kv3 header (e.g. supplied by the plugin from the installed SDK).</param>
    public static string Write(string name, string meshRelPath, double importScale = 1.0, string? header = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header ?? DefaultHeader);
        sb.AppendLine("{");
        sb.AppendLine("\trootNode =");
        sb.AppendLine("\t{");
        sb.AppendLine("\t\t_class = \"RootNode\"");
        sb.AppendLine("\t\tchildren =");
        sb.AppendLine("\t\t[");
        sb.AppendLine("\t\t\t{");
        sb.AppendLine("\t\t\t\t_class = \"RenderMeshFile\"");
        sb.AppendLine($"\t\t\t\tname = \"{name}\"");
        sb.AppendLine($"\t\t\t\tfilename = \"{meshRelPath}\"");
        sb.AppendLine($"\t\t\t\timport_scale = {importScale.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine("\t\t\t},");
        sb.AppendLine("\t\t]");
        sb.AppendLine("\t}");
        sb.AppendLine("\tmodel_archetype = \"\"");
        sb.AppendLine("\tprimary_associated_entity = \"\"");
        sb.AppendLine("\tanim_graph_name = \"\"");
        sb.AppendLine("\tbase_model_name = \"\"");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
