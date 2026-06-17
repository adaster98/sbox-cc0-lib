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
/// and a model/texture filter, shown as a reflowing grid of thumbnail previews. Each cell is
/// draggable straight into the scene using the same payload the native asset/cloud browser uses
/// (<c>DragData.Text</c> = asset path), so models spawn and materials apply as if dragged from it.
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

	private const float GridSpacing = 6f;

	private AssetFilter _filter = AssetFilter.All;
	private string _searchText = "";
	private List<ManifestEntry> _entries = new();
	private List<ManifestEntry> _visible = new();
	private readonly List<AssetCell> _cells = new();
	private int _columns = -1;

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
		_visible = FilteredEntries().ToList();

		// Show the placeholder whenever nothing is listed, wording it for "none imported" vs "no matches".
		_empty.Visible = _visible.Count == 0;
		_scroll.Visible = _visible.Count > 0;
		_empty.Text = _entries.Count == 0
			? "No imported assets yet.\nUse the desktop app to import models or textures into this project."
			: "No matches.";

		_columns = -1;     // force a fresh grid even when the column count is unchanged
		Relayout();
	}

	/// <summary>How many fixed-width cells fit across the current width.</summary>
	private int ComputeColumns()
	{
		var width = _scroll.Width;
		if ( width <= 1f )
			width = Width;
		// Leave room for the scrollbar so the last column never clips.
		return Math.Max( 1, (int)((width - 16f) / (AssetCell.CellWidth + GridSpacing)) );
	}

	/// <summary>Lay the visible cells out in a left-packed grid of thumbnail previews.</summary>
	private void Relayout()
	{
		var columns = ComputeColumns();
		_columns = columns;

		var canvas = new Widget( _scroll, false );
		var grid = Layout.Grid();
		grid.HorizontalSpacing = GridSpacing;
		grid.VerticalSpacing = GridSpacing;
		grid.Margin = 4;
		canvas.Layout = grid;

		_cells.Clear();
		for ( var i = 0; i < _visible.Count; i++ )
		{
			var cell = new AssetCell( canvas, _visible[i] );
			_cells.Add( cell );
			grid.AddCell( i % columns, i / columns, cell );
		}

		// A trailing stretch column absorbs the slack so the fixed-size cells stay left-packed.
		if ( _visible.Count > 0 )
		{
			var stretch = new float[columns + 1];
			stretch[columns] = 1f;
			grid.SetColumnStretch( stretch );
		}

		_scroll.Canvas = canvas;
	}

	protected override void OnResize()
	{
		base.OnResize();
		if ( _visible.Count > 0 && ComputeColumns() != _columns )
			Relayout();
	}

	// AssetSystem listeners are auto-registered for widgets.
	void AssetSystem.IEventListener.OnAssetSystemChanges() => Reload();

	// A freshly-imported asset's thumbnail is rendered in the background after compile. Route the
	// event to the owning cell so its preview appears without the user pressing the refresh button.
	void AssetSystem.IEventListener.OnAssetThumbGenerated( Asset asset )
	{
		foreach ( var cell in _cells )
			cell.OnThumbGenerated( asset );
	}

	// ---- one asset tile: a draggable, self-painted thumbnail preview + name + type ----

	private sealed class AssetCell : Widget
	{
		public const float CellWidth = 104f;
		private const float Padding = 4f;
		private const float ThumbSize = CellWidth - Padding * 2f;   // 96px square preview
		private const float NameHeight = 16f;
		private const float TypeHeight = 14f;
		public const float CellHeight = Padding + ThumbSize + 2f + NameHeight + TypeHeight + Padding;

		private static readonly Color HoverColor = new( 0.23f, 0.25f, 0.30f, 1f );
		private static readonly Color ThumbBackColor = new( 0.10f, 0.10f, 0.12f, 1f );
		private static readonly Color NameColor = new( 0.92f, 0.92f, 0.92f, 1f );
		private static readonly Color TypeColor = new( 0.55f, 0.55f, 0.60f, 1f );

		private readonly ManifestEntry _entry;
		private readonly string _typeLabel;
		private Asset? _asset;
		private Pixmap? _thumb;

		public AssetCell( Widget parent, ManifestEntry entry ) : base( parent, false )
		{
			_entry = entry;
			FixedWidth = CellWidth;
			FixedHeight = CellHeight;
			IsDraggable = true;
			Cursor = CursorShape.Finger;
			ToolTip = entry.Name;

			_asset = AssetSystem.FindByPath( entry.PrimaryAsset );
			_thumb = _asset?.GetAssetThumb( true );
			_typeLabel = _asset?.AssetType?.FriendlyName ?? (entry.IsModel ? "Model" : "Material");
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

			if ( IsUnderMouse )
			{
				Paint.SetBrushAndPen( HoverColor );
				Paint.DrawRect( LocalRect, 4f );
			}

			var thumbRect = new Rect( LocalRect.Left + Padding, LocalRect.Top + Padding, ThumbSize, ThumbSize );
			if ( _thumb is not null )
			{
				Paint.Draw( thumbRect, _thumb, 1f, 4f );
			}
			else
			{
				Paint.SetBrushAndPen( ThumbBackColor );
				Paint.DrawRect( thumbRect, 4f );
				Paint.Pen = TypeColor;
				Paint.DrawIcon( thumbRect, _entry.IsModel ? "view_in_ar" : "image", 36f, TextFlag.Center );
			}

			var nameRect = new Rect( LocalRect.Left + 1f, thumbRect.Bottom + 2f, CellWidth - 2f, NameHeight );
			Paint.Pen = NameColor;
			Paint.DrawText( nameRect, _entry.Name, TextFlag.Center );

			var typeRect = new Rect( LocalRect.Left + 1f, nameRect.Bottom, CellWidth - 2f, TypeHeight );
			Paint.Pen = TypeColor;
			Paint.DrawText( typeRect, _typeLabel, TextFlag.Center );
		}

		/// <summary>Pick up this cell's preview once its thumbnail finishes generating in the background.</summary>
		public void OnThumbGenerated( Asset asset )
		{
			_asset ??= AssetSystem.FindByPath( _entry.PrimaryAsset );
			if ( _asset is null || asset is null || _asset != asset )
				return;

			var thumb = _asset.GetAssetThumb( true );
			if ( thumb is not null )
			{
				_thumb = thumb;
				Update();
			}
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
