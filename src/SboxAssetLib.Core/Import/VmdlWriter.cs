using System.Text;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Import;

/// <summary>Maps one source mesh material name to an addon material.</summary>
public sealed record MaterialRemap(string From, string To);

/// <summary>
/// Generates a Source 2 / s&amp;box <c>.vmdl</c> (ModelDoc) around an imported mesh source.
/// The shape mirrors ModelDoc-authored prop models so the editor opens it in-place.
/// </summary>
public static class VmdlWriter
{
    public const string DefaultHeader =
        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} " +
        "format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->";

    /// <param name="name">Logical model name (the asset id).</param>
    /// <param name="meshRelPath">Addon-relative path to the mesh source, e.g. "models/nature/rock_01/rock_01.fbx".</param>
    /// <param name="materialRelPath">Addon-relative material path used as the global default material.</param>
    /// <param name="modelScale">Global ModelDoc scale. s&amp;box units are inch-based, so centimeter sources use 0.3937.</param>
    /// <param name="header">Override the kv3 header if a newer editor-authored header is needed.</param>
    /// <param name="materialRemaps">Per-slot replacements. When present, the global default is disabled.</param>
    public static string Write(
        string name,
        string meshRelPath,
        string? materialRelPath = null,
        double modelScale = FormatPrefs.DefaultModelImportScale,
        string? header = null,
        IReadOnlyList<MaterialRemap>? materialRemaps = null)
    {
        var material = string.IsNullOrWhiteSpace(materialRelPath) ? "materials/default.vmat" : materialRelPath;
        var remaps = materialRemaps ?? [];
        var scale = modelScale.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine(header ?? DefaultHeader);
        sb.AppendLine("{");
        sb.AppendLine("\trootNode = ");
        sb.AppendLine("\t{");
        sb.AppendLine("\t\t_class = \"RootNode\"");
        sb.AppendLine("\t\tchildren = ");
        sb.AppendLine("\t\t[");
        sb.AppendLine("\t\t\t{");
        sb.AppendLine("\t\t\t\t_class = \"MaterialGroupList\"");
        sb.AppendLine("\t\t\t\tchildren = ");
        sb.AppendLine("\t\t\t\t[");
        sb.AppendLine("\t\t\t\t\t{");
        sb.AppendLine("\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"");
        if (remaps.Count == 0)
        {
            sb.AppendLine("\t\t\t\t\t\tremaps = [  ]");
        }
        else
        {
            sb.AppendLine("\t\t\t\t\t\tremaps = ");
            sb.AppendLine("\t\t\t\t\t\t[");
            foreach (var remap in remaps)
            {
                sb.AppendLine("\t\t\t\t\t\t\t{");
                sb.AppendLine($"\t\t\t\t\t\t\t\tfrom = \"{Escape(remap.From)}\"");
                sb.AppendLine($"\t\t\t\t\t\t\t\tto = \"{Escape(remap.To)}\"");
                sb.AppendLine("\t\t\t\t\t\t\t},");
            }
            sb.AppendLine("\t\t\t\t\t\t]");
        }
        sb.AppendLine($"\t\t\t\t\t\tuse_global_default = {(remaps.Count == 0 ? "true" : "false")}");
        sb.AppendLine($"\t\t\t\t\t\tglobal_default_material = \"{material}\"");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t]");
        sb.AppendLine("\t\t\t},");
        sb.AppendLine("\t\t\t{");
        sb.AppendLine("\t\t\t\t_class = \"PhysicsShapeList\"");
        sb.AppendLine("\t\t\t\tchildren = ");
        sb.AppendLine("\t\t\t\t[");
        sb.AppendLine("\t\t\t\t\t{");
        sb.AppendLine("\t\t\t\t\t\t_class = \"PhysicsMeshFromRender\"");
        sb.AppendLine("\t\t\t\t\t\tparent_bone = \"\"");
        sb.AppendLine("\t\t\t\t\t\tsurface_prop = \"default\"");
        sb.AppendLine("\t\t\t\t\t\tcollision_tags = \"solid\"");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t]");
        sb.AppendLine("\t\t\t},");
        sb.AppendLine("\t\t\t{");
        sb.AppendLine("\t\t\t\t_class = \"RenderMeshList\"");
        sb.AppendLine("\t\t\t\tchildren = ");
        sb.AppendLine("\t\t\t\t[");
        sb.AppendLine("\t\t\t\t\t{");
        sb.AppendLine("\t\t\t\t\t\t_class = \"RenderMeshFile\"");
        sb.AppendLine($"\t\t\t\t\t\tname = \"{name}\"");
        sb.AppendLine($"\t\t\t\t\t\tfilename = \"{meshRelPath}\"");
        sb.AppendLine("\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]");
        sb.AppendLine("\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]");
        sb.AppendLine("\t\t\t\t\t\timport_scale = 1.0");
        sb.AppendLine("\t\t\t\t\t\talign_origin_x_type = \"None\"");
        sb.AppendLine("\t\t\t\t\t\talign_origin_y_type = \"None\"");
        sb.AppendLine("\t\t\t\t\t\talign_origin_z_type = \"None\"");
        sb.AppendLine("\t\t\t\t\t\tparent_bone = \"\"");
        sb.AppendLine("\t\t\t\t\t\timport_filter = ");
        sb.AppendLine("\t\t\t\t\t\t{");
        sb.AppendLine("\t\t\t\t\t\t\texclude_by_default = false");
        sb.AppendLine("\t\t\t\t\t\t\texception_list = [  ]");
        sb.AppendLine("\t\t\t\t\t\t}");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t]");
        sb.AppendLine("\t\t\t},");
        sb.AppendLine("\t\t\t{");
        sb.AppendLine("\t\t\t\t_class = \"ModelModifierList\"");
        sb.AppendLine("\t\t\t\tchildren = ");
        sb.AppendLine("\t\t\t\t[");
        sb.AppendLine("\t\t\t\t\t{");
        sb.AppendLine("\t\t\t\t\t\t_class = \"ModelModifier_ScaleAndMirror\"");
        sb.AppendLine($"\t\t\t\t\t\tscale = {scale}");
        sb.AppendLine("\t\t\t\t\t\tmirror_x = false");
        sb.AppendLine("\t\t\t\t\t\tmirror_y = false");
        sb.AppendLine("\t\t\t\t\t\tmirror_z = false");
        sb.AppendLine("\t\t\t\t\t\tflip_bone_forward = false");
        sb.AppendLine("\t\t\t\t\t\tswap_left_and_right_bones = false");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t]");
        sb.AppendLine("\t\t\t},");
        sb.AppendLine("\t\t]");
        sb.AppendLine("\t\tmodel_archetype = \"\"");
        sb.AppendLine("\t\tprimary_associated_entity = \"\"");
        sb.AppendLine("\t\tanim_graph_name = \"\"");
        sb.AppendLine("\t\tbase_model_name = \"\"");
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
