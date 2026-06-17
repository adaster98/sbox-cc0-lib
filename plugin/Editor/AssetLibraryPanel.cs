#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace SboxAssetLib.Plugin;

/// <summary>
/// Dockable editor panel that lists the assets this project had imported by the standalone
/// Asset Library app (read from the project's <c>library.json</c> manifest), with a search box
/// and a model/texture filter. Each cell is draggable straight into the scene using the same
/// drag payload (<see cref="DragAssetData"/>) the native asset/cloud browser uses, so models
/// spawn and materials apply exactly as if dragged from the built-in browser.
///
/// Lives under <c>Editor/</c> because it uses editor-only APIs (Widget, AssetSystem, Drag).
/// </summary>
[Dock( "Editor", "Asset Library", "inventory_2" )]
public sealed class AssetLibraryPanel : Widget, AssetSystem.IEventListener
{
	private enum AssetFilter { All, Models, Textures }

	private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

	private readonly LineEdit _search;
	private readonly ScrollArea _scroll;
	private readonly Label _empty;
	private readonly List<(Button Button, AssetFilter Filter)> _filterButtons = new();

	private AssetFilter _filter = AssetFilter.All;
	private string _searchText = "";
	private List<ManifestEntry> _entries = new();

	public AssetLibraryPanel( Widget parent ) : base( parent, false )
	{
		Layout = Layout.Column();
		Layout.Margin = 4;
		Layout.Spacing = 4;

		// Search + refresh row.
		var top = Layout.AddRow();
		top.Spacing = 4;
		_search = top.Add( new LineEdit( this ) { PlaceholderText = "Search imported assets…", ClearButtonEnabled = true }, 1 );
		_search.TextEdited += text => { _searchText = text ?? ""; RebuildList(); };
		top.Add( new IconButton( "refresh", Reload, this ) );

		// Model/texture filter row.
		var filters = Layout.AddRow();
		filters.Spacing = 4;
		AddFilterButton( filters, "All", AssetFilter.All );
		AddFilterButton( filters, "Models", AssetFilter.Models );
		AddFilterButton( filters, "Textures", AssetFilter.Textures );
		filters.AddStretchCell();

		_scroll = Layout.Add( new ScrollArea( this ), 1 );

		_empty = Layout.Add( new Label(
			"No imported assets yet.\nUse the desktop app to import models or textures into this project.", this ) );

		UpdateFilterStyles();
		Reload();
	}

	private void AddFilterButton( Layout row, string text, AssetFilter filter )
	{
		var button = row.Add( new Button( text, this ) );
		button.Clicked += () =>
		{
			_filter = filter;
			UpdateFilterStyles();
			RebuildList();
		};
		_filterButtons.Add( (button, filter) );
	}

	private void UpdateFilterStyles()
	{
		foreach ( var (button, filter) in _filterButtons )
			button.SetStyles( filter == _filter ? "background-color: #3478f6; color: white;" : "" );
	}

	/// <summary>Re-read the manifest from disk and rebuild the list.</summary>
	private void Reload()
	{
		_entries = LoadManifest();
		RebuildList();
	}

	private static List<ManifestEntry> LoadManifest()
	{
		try
		{
			var root = Project.Current?.GetAssetsPath();
			if ( string.IsNullOrEmpty( root ) )
				return new();

			var path = Path.Combine( root, "library.json" );
			if ( !File.Exists( path ) )
				return new();

			var manifest = JsonSerializer.Deserialize<Manifest>( File.ReadAllText( path ), JsonOpts );
			var entries = manifest?.Assets ?? new();

			// Drop entries whose source asset has since been deleted from the project.
			return entries
				.Where( e => !string.IsNullOrEmpty( e.PrimaryAsset ) && File.Exists( Path.Combine( root, e.PrimaryAsset ) ) )
				.OrderBy( e => e.Name, StringComparer.OrdinalIgnoreCase )
				.ToList();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AssetLibrary] failed to read manifest: {ex.Message}" );
			return new();
		}
	}

	private IEnumerable<ManifestEntry> FilteredEntries()
	{
		foreach ( var entry in _entries )
		{
			if ( _filter == AssetFilter.Models && !entry.IsModel ) continue;
			if ( _filter == AssetFilter.Textures && entry.IsModel ) continue;
			if ( _searchText.Length > 0 && !entry.Name.Contains( _searchText, StringComparison.OrdinalIgnoreCase ) ) continue;
			yield return entry;
		}
	}

	private void RebuildList()
	{
		// Rebuild by reassigning the scroll canvas — avoids stale widget references.
		var canvas = new Widget( _scroll, false );
		canvas.Layout = Layout.Column();
		canvas.Layout.Spacing = 2;

		var any = false;
		foreach ( var entry in FilteredEntries() )
		{
			canvas.Layout.Add( new AssetCell( canvas, entry ) );
			any = true;
		}
		canvas.Layout.AddStretchCell();

		_scroll.Canvas = canvas;

		// Show the placeholder whenever nothing is listed, wording it for "none imported" vs "no matches".
		_empty.Visible = !any;
		_scroll.Visible = any;
		_empty.Text = _entries.Count == 0
			? "No imported assets yet.\nUse the desktop app to import models or textures into this project."
			: "No matches.";
	}

	// AssetSystem listeners are auto-registered for widgets.
	void AssetSystem.IEventListener.OnAssetSystemChanges() => Reload();
	void AssetSystem.IEventListener.OnAssetThumbGenerated( Asset asset ) => Update();

	// ---- one asset row: draggable, self-painted thumbnail + name ----

	private sealed class AssetCell : Widget
	{
		private static readonly Color RowColor = new( 0.17f, 0.18f, 0.22f, 1f );
		private static readonly Color RowHoverColor = new( 0.23f, 0.25f, 0.30f, 1f );
		private static readonly Color TextColor = new( 0.92f, 0.92f, 0.92f, 1f );
		private static readonly Color IconColor = new( 0.55f, 0.55f, 0.60f, 1f );

		private readonly ManifestEntry _entry;
		private Asset? _asset;
		private Pixmap? _thumb;

		public AssetCell( Widget parent, ManifestEntry entry ) : base( parent, false )
		{
			_entry = entry;
			FixedHeight = 56;
			IsDraggable = true;
			Cursor = CursorShape.Finger;
			ToolTip = entry.PrimaryAsset;

			_asset = AssetSystem.FindByPath( entry.PrimaryAsset );
			_thumb = _asset?.GetAssetThumb( true );
		}

		protected override void OnDragStart()
		{
			if ( _asset is null )
				return;

			// s&box interprets DragData.Text as newline-separated asset paths (DragData.Assets parses
			// each via AssetSystem.FindByPath). This is the same payload the native asset/cloud browser
			// produces, so the scene drop target spawns models / applies materials as usual.
			var drag = new Drag( this );
			drag.Data.Text = _asset.Path;
			if ( _thumb is not null )
				drag.SetImage( _thumb );
			drag.Execute();
		}

		protected override void OnPaint()
		{
			// Grab the thumbnail once it's been generated in the background.
			_thumb ??= _asset?.GetAssetThumb( false );

			Paint.Antialiasing = true;
			Paint.SetBrushAndPen( IsUnderMouse ? RowHoverColor : RowColor );
			Paint.DrawRect( LocalRect, 4f );

			var thumbRect = new Rect( LocalRect.Left + 4f, LocalRect.Top + 4f, 48f, 48f );
			if ( _thumb is not null )
			{
				Paint.Draw( thumbRect, _thumb, 1f, 4f );
			}
			else
			{
				Paint.Pen = IconColor;
				Paint.DrawIcon( thumbRect, _entry.IsModel ? "view_in_ar" : "image", 26f, TextFlag.Center );
			}

			var textRect = new Rect(
				thumbRect.Right + 8f, LocalRect.Top,
				LocalRect.Width - (thumbRect.Right + 8f) - 8f, LocalRect.Height );
			Paint.Pen = TextColor;
			Paint.DrawText( textRect, _entry.Name, TextFlag.LeftCenter );
		}

		protected override void OnMouseEnter() => Update();
		protected override void OnMouseLeave() => Update();

		protected override void OnContextMenu( ContextMenuEvent e )
		{
			var menu = new ContextMenu( this );
			if ( _entry.IsModel )
				menu.AddOption( "Spawn in scene", "add", () => AssetLibScene.SpawnModel( _asset?.Path ?? _entry.PrimaryAsset ) );
			menu.AddOption( "Show in Asset Browser", "search", () =>
			{
				try { EditorEvent.Run( "assetsystem.highlight", _asset?.Path ?? _entry.PrimaryAsset ); }
				catch ( Exception ex ) { Log.Warning( $"[AssetLibrary] highlight failed: {ex.Message}" ); }
			} );
			menu.OpenAtCursor();
		}
	}

	// ---- minimal mirror of SboxAssetLib.Core.Import.LibraryManifest (the plugin can't
	//      reference Core.dll inside the s&box sandbox, so keep these fields in sync) ----

	private sealed class Manifest
	{
		public List<ManifestEntry> Assets { get; set; } = new();
	}

	private sealed class ManifestEntry
	{
		public string Provider { get; set; } = "";
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public string Kind { get; set; } = "";
		public string PrimaryAsset { get; set; } = "";

		public bool IsModel => string.Equals( Kind, "Model", StringComparison.OrdinalIgnoreCase );
	}
}
