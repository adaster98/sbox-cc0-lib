#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Editor;
using Sandbox;

namespace SboxAssetLib.Plugin;

/// <summary>
/// Editor-side of the s&amp;box Asset Library bridge.
///
/// Runs a lightweight file IPC loop:
///   1. writes <c>status.json</c> every ~2s advertising the open project + asset path;
///   2. polls the requests folder for import requests from the standalone app;
///   3. compiles the imported asset and optionally spawns models into the active scene.
///
/// The source must live under the addon's <c>Editor/</c> folder. s&amp;box compiles <c>Code/</c>
/// as the game/library assembly, which does not reference editor-only APIs like
/// <see cref="AssetSystem"/> and <see cref="SceneEditorSession"/>.
/// </summary>
public static class AssetLibBridge
{
	// Mirror of SboxAssetLib.Core.Bridge.BridgePaths. The plugin can't reference the
	// app's Core.dll inside the s&box sandbox, so keep this path contract in sync.
	private static readonly string Root =
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "sbox-asset-lib", "bridge" );
	private static string StatusFile => Path.Combine( Root, "status.json" );
	private static string RequestsDir => Path.Combine( Root, "requests" );
	private static string ResponsesDir => Path.Combine( Root, "responses" );

	private static readonly Queue<PendingRequest> PendingRequests = new();
	private static readonly object QueueLock = new();
	private static readonly Encoding Utf8NoBom = new UTF8Encoding( false );

	private static Timer? _pollTimer;
	private static bool _running;
	private static DateTime _lastStatusUtc = DateTime.MinValue;

	public static bool IsRunning => _running;

	[Menu( "Editor", "Asset Library/Start Bridge" )]
	public static void Start()
	{
		if ( _running )
			return;

		Directory.CreateDirectory( RequestsDir );
		Directory.CreateDirectory( ResponsesDir );

		_running = true;
		_lastStatusUtc = DateTime.MinValue;
		_pollTimer = new Timer( ReadRequestFiles, null, 0, 500 );

		WriteStatus();
		Log.Info( "[AssetLibBridge] started - listening at " + Root );
	}

	[Menu( "Editor", "Asset Library/Stop Bridge" )]
	public static void Stop()
	{
		_running = false;
		_pollTimer?.Dispose();
		_pollTimer = null;

		lock ( QueueLock )
		{
			PendingRequests.Clear();
		}

		try { File.Delete( StatusFile ); } catch { }
		Log.Info( "[AssetLibBridge] stopped" );
	}

	[EditorEvent.Frame]
	public static void OnEditorFrame()
	{
		if ( !_running )
			return;

		if ( DateTime.UtcNow - _lastStatusUtc > TimeSpan.FromSeconds( 2 ) )
		{
			WriteStatus();
			_lastStatusUtc = DateTime.UtcNow;
		}

		ProcessPendingRequests();
	}

	/// <summary>
	/// Timer thread: only touches files and queues work. Editor APIs are drained from OnEditorFrame.
	/// </summary>
	private static void ReadRequestFiles( object? state )
	{
		if ( !_running )
			return;

		try
		{
			foreach ( var file in Directory.GetFiles( RequestsDir, "*.json" ) )
			{
				try
				{
					var json = File.ReadAllText( file, Utf8NoBom );
					var id = Path.GetFileNameWithoutExtension( file );
					File.Delete( file );

					lock ( QueueLock )
					{
						PendingRequests.Enqueue( new PendingRequest( id, json ) );
					}
				}
				catch ( IOException ) { }
				catch ( Exception ex )
				{
					Log.Warning( $"[AssetLibBridge] request read error: {ex.Message}" );
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AssetLibBridge] request poll error: {ex.Message}" );
		}
	}

	private static void ProcessPendingRequests()
	{
		while ( true )
		{
			PendingRequest pending;
			lock ( QueueLock )
			{
				if ( PendingRequests.Count == 0 )
					return;

				pending = PendingRequests.Dequeue();
			}

			ProcessRequest( pending );
		}
	}

	private static void WriteStatus()
	{
		var status = new StatusDto
		{
			Ok = true,
			ProjectName = GetProjectName(),
			ContentPath = GetContentPath(),
			Version = "0.1",
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		WriteJsonAtomic( StatusFile, status );
	}

	private static void ProcessRequest( PendingRequest pending )
	{
		ResultDto result;
		try
		{
			var req = JsonSerializer.Deserialize<RequestDto>( pending.Json, JsonOpts )
			          ?? throw new Exception( "empty request" );

			// Register + compile the source dependencies before the primary asset, so its children
			// and dependencies already exist and are known to the AssetSystem when it compiles.
			// Otherwise the engine leaves the parent referencing missing children (materials: red
			// checkers) or out-of-date/"stopped existing" dependencies (models: missing mesh), and
			// recompiles on demand forever until a manual editor save or restart. Order matters:
			// textures feed materials, and materials + meshes feed the model.
			for ( var stage = 0; stage <= MaxDependencyStage; stage++ )
			{
				foreach ( var dep in req.Files )
				{
					if ( string.Equals( dep, req.PrimaryAsset, StringComparison.OrdinalIgnoreCase ) )
						continue;
					if ( DependencyStage( dep ) == stage )
						CompileAsset( dep );
				}
			}

			var compiled = CompileAsset( req.PrimaryAsset );
			if ( req.SpawnInScene && string.Equals( req.Kind, "Model", StringComparison.OrdinalIgnoreCase ) )
				AssetLibScene.SpawnModel( compiled ?? req.PrimaryAsset );

			result = new ResultDto { RequestId = pending.Id, Ok = true, CompiledAsset = compiled };
		}
		catch ( Exception ex )
		{
			result = new ResultDto { RequestId = pending.Id, Ok = false, Error = ex.Message };
		}

		WriteJsonAtomic( Path.Combine( ResponsesDir, pending.Id + ".json" ), result );
	}

	/// <summary>The open project's asset root (where imported assets should be written).</summary>
	private static string? GetContentPath()
	{
		try { return Project.Current?.GetAssetsPath(); }
		catch { return null; }
	}

	private static string? GetProjectName()
	{
		try { return Project.Current?.Config?.Title; }
		catch { return null; }
	}

	private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".exr", ".bmp" };
	private static readonly string[] MeshExtensions = { ".fbx", ".obj", ".gltf", ".glb", ".dmx" };

	private const int MaxDependencyStage = 2;

	// Compile order for a request's source files: textures (0) feed materials, then meshes (1) and
	// materials (2) feed the primary model. Anything else returns -1 and is skipped here.
	private static int DependencyStage( string path )
	{
		var ext = Path.GetExtension( path ).ToLowerInvariant();
		if ( Array.IndexOf( TextureExtensions, ext ) >= 0 ) return 0;
		if ( Array.IndexOf( MeshExtensions, ext ) >= 0 ) return 1;
		if ( ext == ".vmat" ) return 2;
		return -1;
	}

	/// <summary>Register and compile the newly-written asset, returning its resource path.</summary>
	private static string? CompileAsset( string addonRelativePath )
	{
		try
		{
			var content = GetContentPath();
			var assetPath = addonRelativePath.Replace( '\\', '/' );
			var absolutePath = content is null ? null : Path.GetFullPath( Path.Combine( content, assetPath ) );

			Asset? asset = null;
			if ( absolutePath is not null && File.Exists( absolutePath ) )
			{
				asset = AssetSystem.RegisterFile( absolutePath )
					?? AssetSystem.RegisterFile( absolutePath.Replace( '\\', '/' ) );
			}

			asset ??= AssetSystem.FindByPath( assetPath );
			if ( asset is null )
				return assetPath;

			asset.Compile( true );
			return asset.RelativePath ?? asset.Path ?? assetPath;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AssetLibBridge] compile nudge failed (asset may still auto-compile): {ex.Message}" );
			return addonRelativePath;
		}
	}

	private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

	private static void WriteJsonAtomic( string path, object value )
	{
		var tmp = path + ".tmp";
		File.WriteAllText( tmp, JsonSerializer.Serialize( value, JsonOpts ), Utf8NoBom );
		File.Move( tmp, path, overwrite: true );
	}

	private readonly record struct PendingRequest( string Id, string Json );

	private sealed class StatusDto
	{
		public bool Ok { get; set; }
		public string? ProjectName { get; set; }
		public string? ContentPath { get; set; }
		public string? Version { get; set; }
		public DateTimeOffset UpdatedAt { get; set; }
	}

	private sealed class RequestDto
	{
		public string RequestId { get; set; } = "";
		public string PrimaryAsset { get; set; } = "";
		public string Kind { get; set; } = "";
		public List<string> Files { get; set; } = new();
		public bool SpawnInScene { get; set; }
	}

	private sealed class ResultDto
	{
		public string? RequestId { get; set; }
		public bool Ok { get; set; }
		public string? CompiledAsset { get; set; }
		public string? Error { get; set; }
	}
}
