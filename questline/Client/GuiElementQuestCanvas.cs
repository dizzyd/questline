using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace questline.Client;

/// <summary>
/// A pannable, zoomable chapter tree. Edges and node frames live in two meshes built in
/// canvas units, so panning and zooming only change the model-view matrix; item icons and
/// labels are drawn per node, for the nodes in view only.
/// </summary>
public class GuiElementQuestCanvas : GuiElement
{
    /// <summary>Canvas units per grid cell, before zoom and GUI scale.</summary>
    public const float CellSize = 56;
    public const float NodeSize = 40;
    const float EdgeWidth = 3;
    const float MinZoom = 0.2f, MaxZoom = 3f;
    const float LabelMinZoom = 0.9f;
    const int LabelsPerFrame = 8;
    const int AtlasCell = 64;

    // Fill and border per QuestNodeState, in enum order.
    static readonly double[][] FrameFill =
    [
        [0.13, 0.13, 0.13, 0.95],
        [0.22, 0.20, 0.16, 0.95],
        [0.16, 0.20, 0.28, 0.95],
        [0.30, 0.24, 0.08, 0.95],
        [0.14, 0.26, 0.14, 0.95],
    ];
    static readonly double[][] FrameStroke =
    [
        [0.35, 0.35, 0.35, 1],
        [0.85, 0.78, 0.60, 1],
        [0.45, 0.65, 0.95, 1],
        [1.00, 0.80, 0.20, 1],
        [0.40, 0.85, 0.40, 1],
    ];
    static readonly int LitEdge = ColorUtil.ColorFromRgba(220, 190, 110, 255);
    static readonly int DimEdge = ColorUtil.ColorFromRgba(90, 90, 90, 255);
    static readonly Vec4f White = new(1, 1, 1, 1);

    readonly List<QuestCanvasNode> nodes;
    readonly Matrixf mvMat = new();
    readonly Dictionary<int, LoadedTexture> labels = new();
    readonly CairoFont labelFont = CairoFont.WhiteDetailText();
    readonly float contentWidth, contentHeight;

    MeshRef edgeMesh, frameMesh;
    LoadedTexture frameAtlas;
    bool meshesDirty = true;

    // Canvas units at the top left corner of the element.
    float panX, panY;
    float zoom = 1;
    bool dragging;
    int prevMouseX, prevMouseY;

    float tourSeconds, tourElapsed;
    Action<CanvasFrameStats> onTourDone;

    public CanvasFrameStats Stats { get; } = new();
    public float Zoom => zoom;
    public int VisibleNodes { get; private set; }
    public bool Touring => onTourDone != null;

    float ViewScale => zoom * RuntimeEnv.GUIScale;

    public GuiElementQuestCanvas(ICoreClientAPI capi, ElementBounds bounds, List<QuestCanvasNode> nodes) : base(capi, bounds)
    {
        this.nodes = nodes;
        foreach (var n in nodes)
        {
            contentWidth = Math.Max(contentWidth, n.X * CellSize);
            contentHeight = Math.Max(contentHeight, n.Y * CellSize);
        }
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();

        ctxStatic.SetSourceRGBA(0, 0, 0, 0.35);
        ctxStatic.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.OuterWidth, Bounds.OuterHeight);
        ctxStatic.Fill();

        ComposeFrameAtlas();
        panX = -NodeSize;
        panY = -NodeSize;
    }

    void ComposeFrameAtlas()
    {
        int states = FrameFill.Length;
        using var atlas = new ImageSurface(Format.Argb32, AtlasCell * states, AtlasCell);
        using var ctx = new Context(atlas);
        for (int i = 0; i < states; i++)
        {
            RoundRectangle(ctx, i * AtlasCell + 4, 4, AtlasCell - 8, AtlasCell - 8, 8);
            var f = FrameFill[i];
            ctx.SetSourceRGBA(f[0], f[1], f[2], f[3]);
            ctx.FillPreserve();
            var s = FrameStroke[i];
            ctx.SetSourceRGBA(s[0], s[1], s[2], s[3]);
            ctx.LineWidth = 4;
            ctx.Stroke();
        }

        frameAtlas ??= new LoadedTexture(api);
        api.Gui.LoadOrUpdateCairoTexture(atlas, true, ref frameAtlas);
    }

    /// <summary>Call after any node's state or position changes.</summary>
    public void MarkDirty() => meshesDirty = true;

    void RebuildMeshes()
    {
        int edgeCount = 0;
        foreach (var n in nodes) edgeCount += n.Prereqs.Length;

        var edges = new MeshData(edgeCount * 4, edgeCount * 6, false, true, true, false);
        foreach (var n in nodes)
        {
            foreach (int p in n.Prereqs)
            {
                var from = nodes[p];
                int color = from.State == QuestNodeState.Done ? LitEdge : DimEdge;
                AddSegment(edges, from.X * CellSize, from.Y * CellSize, n.X * CellSize, n.Y * CellSize, color);
            }
        }

        int states = FrameFill.Length;
        var frames = new MeshData(nodes.Count * 4, nodes.Count * 6, false, true, true, false);
        const float half = NodeSize / 2;
        foreach (var n in nodes)
        {
            float cx = n.X * CellSize, cy = n.Y * CellSize;
            float u0 = (float)n.State / states, u1 = ((float)n.State + 1) / states;
            int b = frames.VerticesCount;
            frames.AddVertex(cx - half, cy - half, 0, u0, 0, ColorUtil.WhiteArgb);
            frames.AddVertex(cx + half, cy - half, 0, u1, 0, ColorUtil.WhiteArgb);
            frames.AddVertex(cx + half, cy + half, 0, u1, 1, ColorUtil.WhiteArgb);
            frames.AddVertex(cx - half, cy + half, 0, u0, 1, ColorUtil.WhiteArgb);
            frames.AddIndices(b, b + 1, b + 2, b, b + 2, b + 3);
        }

        DeleteMeshes();
        if (edgeCount > 0) edgeMesh = api.Render.UploadMesh(edges);
        if (nodes.Count > 0) frameMesh = api.Render.UploadMesh(frames);
    }

    static void AddSegment(MeshData mesh, float x1, float y1, float x2, float y2, int color)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return;
        float nx = -dy / len * EdgeWidth / 2, ny = dx / len * EdgeWidth / 2;

        int b = mesh.VerticesCount;
        mesh.AddVertex(x1 + nx, y1 + ny, 0, 0, 0, color);
        mesh.AddVertex(x2 + nx, y2 + ny, 0, 0, 0, color);
        mesh.AddVertex(x2 - nx, y2 - ny, 0, 0, 0, color);
        mesh.AddVertex(x1 - nx, y1 - ny, 0, 0, 0, color);
        mesh.AddIndices(b, b + 1, b + 2, b, b + 2, b + 3);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        long start = Stopwatch.GetTimestamp();

        if (Touring) AdvanceTour(deltaTime);
        if (meshesDirty)
        {
            RebuildMeshes();
            meshesDirty = false;
        }

        float s = ViewScale;
        double ox = Bounds.renderX - panX * s, oy = Bounds.renderY - panY * s;

        api.Render.PushScissor(Bounds);

        var prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("rgbaIn", White);
        prog.Uniform("extraGlow", 0);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        mvMat.Set(api.Render.CurrentModelviewMatrix).Translate(ox, oy, 50).Scale(s, s, 1);
        prog.UniformMatrix("modelViewMatrix", mvMat.Values);

        if (edgeMesh != null)
        {
            prog.Uniform("applyColor", 1);
            prog.Uniform("noTexture", 1f);
            api.Render.RenderMesh(edgeMesh);
        }

        if (frameMesh != null)
        {
            prog.Uniform("applyColor", 0);
            prog.Uniform("noTexture", 0f);
            prog.BindTexture2D("tex2d", frameAtlas.TextureId, 0);
            api.Render.GlToggleBlend(true, EnumBlendMode.PremultipliedAlpha);
            api.Render.RenderMesh(frameMesh);
            api.Render.GlToggleBlend(true);
        }

        RenderVisibleNodes(s, ox, oy);

        api.Render.PopScissor();

        Stats.Record(deltaTime * 1000, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    void RenderVisibleNodes(float s, double ox, double oy)
    {
        float x2 = panX + (float)Bounds.InnerWidth / s, y2 = panY + (float)Bounds.InnerHeight / s;
        bool showLabels = zoom >= LabelMinZoom;
        int labelBudget = LabelsPerFrame;
        float iconSize = NodeSize * 0.55f * s;
        int visible = 0;

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            float cx = n.X * CellSize, cy = n.Y * CellSize;
            if (cx < panX - NodeSize || cx > x2 + NodeSize || cy < panY - NodeSize || cy > y2 + NodeSize) continue;
            visible++;

            double sx = ox + cx * s, sy = oy + cy * s;
            if (n.Icon?.Itemstack != null)
            {
                api.Render.RenderItemstackToGui(n.Icon, sx, sy, 200, iconSize, ColorUtil.WhiteArgb, true, false, false);
            }

            if (showLabels && GetLabel(i, ref labelBudget) is { } tex)
            {
                api.Render.Render2DTexturePremultipliedAlpha(tex.TextureId,
                    (float)(sx - tex.Width / 2.0), (float)(sy + NodeSize / 2 * s + 2), tex.Width, tex.Height, 250);
            }
        }

        VisibleNodes = visible;
    }

    // Labels are generated lazily, a few per frame, so a first zoom-in doesn't hitch.
    LoadedTexture GetLabel(int index, ref int budget)
    {
        if (labels.TryGetValue(index, out var tex)) return tex;
        if (budget <= 0) return null;
        budget--;
        tex = api.Gui.TextTexture.GenTextTexture(nodes[index].Title, labelFont);
        labels[index] = tex;
        return tex;
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (args.Button != EnumMouseButton.Left) return;

        dragging = true;
        prevMouseX = args.X;
        prevMouseY = args.Y;
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        dragging = false;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!dragging) return;

        float s = ViewScale;
        panX -= (args.X - prevMouseX) / s;
        panY -= (args.Y - prevMouseY) / s;
        prevMouseX = args.X;
        prevMouseY = args.Y;
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)) return;

        ZoomAt(api.Input.MouseX - Bounds.absX, api.Input.MouseY - Bounds.absY, args.delta > 0 ? 1.15f : 1 / 1.15f);
        args.SetHandled(true);
    }

    /// <summary>Zooms by <paramref name="factor"/>, keeping the canvas point under (viewX, viewY) in place.</summary>
    public void ZoomAt(double viewX, double viewY, float factor)
    {
        float s = ViewScale;
        float cx = panX + (float)viewX / s, cy = panY + (float)viewY / s;

        zoom = GameMath.Clamp(zoom * factor, MinZoom, MaxZoom);

        s = ViewScale;
        panX = cx - (float)viewX / s;
        panY = cy - (float)viewY / s;
    }

    /// <summary>
    /// Sweeps the view across the whole tree, zooming in and out, for <paramref name="seconds"/>,
    /// then reports the frame times seen along the way. Lets a test check smoothness without a mouse.
    /// </summary>
    public void StartTour(float seconds, Action<CanvasFrameStats> done)
    {
        tourSeconds = seconds;
        tourElapsed = 0;
        onTourDone = done;
        Stats.Reset();
    }

    void AdvanceTour(float dt)
    {
        tourElapsed += dt;
        float t = tourElapsed / tourSeconds;
        if (t >= 1)
        {
            var done = onTourDone;
            onTourDone = null;
            done(Stats);
            return;
        }

        // Two passes left to right, a slow vertical weave, zooming between fully out and close in.
        zoom = MinZoom + (MaxZoom * 0.75f - MinZoom) * (0.5f - 0.5f * MathF.Cos(t * MathF.PI * 6));
        float s = ViewScale;
        float viewW = (float)Bounds.InnerWidth / s, viewH = (float)Bounds.InnerHeight / s;
        float fx = 0.5f - 0.5f * MathF.Cos(t * MathF.PI * 4);
        float fy = 0.5f - 0.5f * MathF.Cos(t * MathF.PI * 2.5f);
        panX = fx * contentWidth - viewW / 2;
        panY = fy * contentHeight - viewH / 2;
    }

    void DeleteMeshes()
    {
        if (edgeMesh != null) api.Render.DeleteMesh(edgeMesh);
        if (frameMesh != null) api.Render.DeleteMesh(frameMesh);
        edgeMesh = frameMesh = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        DeleteMeshes();
        frameAtlas?.Dispose();
        foreach (var tex in labels.Values) tex.Dispose();
        labels.Clear();
    }
}
