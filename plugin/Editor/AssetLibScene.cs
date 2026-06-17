#nullable enable

using System;
using System.IO;
using Editor;
using Sandbox;

namespace SboxAssetLib.Plugin;

/// <summary>
/// Shared editor-scene helpers used by both the file-IPC bridge and the dockable Asset Library panel.
/// </summary>
internal static class AssetLibScene
{
	/// <summary>Spawn a GameObject with the given (compiled) model into the active scene and select it.</summary>
	public static void SpawnModel( string vmdlPath )
	{
		try
		{
			var session = SceneEditorSession.Active;
			var scene = session?.Scene;
			if ( session is null || scene is null )
			{
				Log.Warning( "[AssetLibrary] no active scene to spawn into." );
				return;
			}

			using ( session.UndoScope( "Spawn Asset Library Model" ).WithGameObjectCreations().Push() )
			{
				var go = scene.CreateObject( true );
				go.Name = Path.GetFileNameWithoutExtension( vmdlPath );

				var renderer = go.AddComponent<ModelRenderer>();
				renderer.Model = Model.Load( vmdlPath.Replace( '\\', '/' ) );

				EditorScene.Selection.Clear();
				EditorScene.Selection.Add( go );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AssetLibrary] spawn failed: {ex.Message}" );
		}
	}
}
