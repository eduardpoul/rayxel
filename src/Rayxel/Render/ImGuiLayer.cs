using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Rayxel.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>
/// Draws Dear ImGui inside our own swapchain.
/// <para>
/// The "inside" is the whole point. Anything composited over this window as a separate
/// window or layer costs a full frame of latency — more than every upscaler in the app
/// combined — so the UI has to be part of the same render pass as the video.
/// </para>
/// </summary>
public sealed class ImGuiLayer : IDisposable
{
    private readonly ID3D11Device _device;
    private IntPtr _context;

    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _ps = null!;
    private ID3D11InputLayout _layout = null!;
    private ID3D11Buffer _constants = null!;
    private ID3D11SamplerState _sampler = null!;
    private ID3D11BlendState _blend = null!;
    private ID3D11RasterizerState _raster = null!;
    private ID3D11DepthStencilState _depth = null!;

    private ID3D11Texture2D? _fontTexture;
    private ID3D11ShaderResourceView? _fontView;

    private float _styleFactor = -1f;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private int _vertexCapacity, _indexCapacity;

    /// <summary>True once the native library loaded and the pipeline built.</summary>
    public bool Available { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The type scale. Valid once <see cref="Available"/> is true.</summary>
    public Ui.Fonts Fonts { get; private set; } = new();

    /// <summary>True while the pointer is over ImGui, so clicks should not reach the video.</summary>
    public bool WantsMouse => Available && ImGui.GetIO().WantCaptureMouse;

    /// <summary>
    /// True only while the user is actually typing into a field. Not the same as ImGui
    /// merely having focus — hotkeys keep working with a panel open, on purpose.
    /// </summary>
    public bool WantsTextInput => Available && ImGui.GetIO().WantTextInput;

    public ImGuiLayer(ID3D11Device device) => _device = device;

    /// <summary>
    /// Builds the context and pipeline. Returns false rather than throwing: a missing
    /// native cimgui, or a shader that will not compile, should cost the overlay and
    /// nothing else. The video is the part that matters.
    /// </summary>
    public bool Initialize(int width, int height)
    {
        try
        {
            _context = ImGui.CreateContext();
            ImGui.SetCurrentContext(_context);

            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(width, height);
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

            // Keyboard nav is deliberately left off. It makes ImGui claim the keyboard
            // whenever a window has focus, which would swallow the app's hotkeys - and in
            // something you use while playing a game, a hotkey that stops working because
            // a panel is open is worse than no keyboard navigation at all.

            // Nothing here is worth writing to disk, and an ini file appearing beside the
            // executable is a surprise the user did not ask for.
            unsafe { io.NativePtr->IniFilename = null; }

            ImGui.StyleColorsDark();

            // Size the atlas for the biggest window this display can give us, so going
            // full screen later does not need the whole thing rebuilt mid-frame.
            int screenHeight = Math.Max(Native.Win32.GetSystemMetrics(Native.Win32.SM_CYSCREEN),
                                        Ui.Layout.DisplayHeight);
            Ui.Layout.SetAtlasFactor(Ui.Layout.ZoomFor(screenHeight));
            Ui.Layout.Update(width, height);
            Ui.Theme.Apply();

            // Before the atlas is built: adding fonts afterwards would need it rebuilt.
            Fonts = new Ui.Fonts();
            Fonts.Build(io);

            CreatePipeline();
            CreateFontTexture();

            Available = true;
            Log.Info($"ImGui {ImGui.GetVersion()} ready, drawing inside the swapchain");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("ImGui unavailable - the overlay is off, video is unaffected", ex);
            return false;
        }
    }

    private void CreatePipeline()
    {
        Compiler.Compile(ShaderSource, [], null, "VS", "imgui.hlsl", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob is null) throw new InvalidOperationException($"ImGui VS compile failed: {vsErr?.AsString()}");
        Compiler.Compile(ShaderSource, [], null, "PS", "imgui.hlsl", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob is null) throw new InvalidOperationException($"ImGui PS compile failed: {psErr?.AsString()}");

        _vs = _device.CreateVertexShader(vsBlob.AsSpan());
        _ps = _device.CreatePixelShader(psBlob.AsSpan());

        // Matches ImDrawVert exactly: two floats, two floats, four bytes.
        _layout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float,   0,  0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,   8,  0),
            new InputElementDescription("COLOR",    0, Format.R8G8B8A8_UNorm, 16, 0),
        ], vsBlob.AsSpan());

        vsBlob.Dispose();
        psBlob.Dispose();

        _constants = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = ConstantBytes,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Always,
            MaxLOD = float.MaxValue
        });

        // Straight alpha, and the destination alpha is accumulated too so the swapchain
        // stays fully opaque where the UI covers the video.
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blend = _device.CreateBlendState(blend);

        _raster = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            ScissorEnable = true,
            DepthClipEnable = true
        });

        _depth = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            StencilEnable = false
        });
    }

    private unsafe void CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int w, out int h, out int bytesPerPixel);

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource
        };

        var data = new SubresourceData((IntPtr)pixels, (uint)(w * bytesPerPixel));
        _fontTexture = _device.CreateTexture2D(desc, [data]);
        _fontView = _device.CreateShaderResourceView(_fontTexture);

        // ImGui hands this back with every draw command; it is our own handle, not a
        // pointer it dereferences, so the SRV's native pointer serves directly.
        io.Fonts.SetTexID(_fontView.NativePointer);
        io.Fonts.ClearTexData();

        Log.Debug($"ImGui font atlas {w}x{h}");
    }

    /// <summary>Starts a frame. Everything drawn until <see cref="Render"/> lands on this one.</summary>
    public void NewFrame(int width, int height, float deltaSeconds)
    {
        if (!Available) return;
        ImGui.SetCurrentContext(_context);

        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(width, height);
        io.DisplayFramebufferScale = Vector2.One;

        // Layout follows the monitor, capped by the window; the glyphs are scaled to match the atlas.
        Ui.Layout.Update(width, height);
        io.FontGlobalScale = Ui.Layout.FontScale;

        // Style metrics are absolute pixels, so they have to be recomputed when the window
        // changes size — going borderless doubles the scale in one frame.
        if (Math.Abs(Ui.Layout.Factor - _styleFactor) > 0.001f)
        {
            _styleFactor = Ui.Layout.Factor;
            Ui.Theme.Apply();
        }
        // A stalled frame must not produce a zero or negative delta: ImGui asserts on it,
        // and at 120 fps the real value is small enough that clamping is invisible.
        io.DeltaTime = Math.Clamp(deltaSeconds, 1f / 1000f, 1f / 10f);

        ImGui.NewFrame();
    }

    /// <summary>
    /// Submits the frame's draw lists into the currently bound render target.
    /// <paramref name="encodePq"/> re-encodes ImGui's sRGB colours for a PQ swapchain;
    /// without it the UI reads as several thousand nits on an HDR display.
    /// </summary>
    public void Render(ID3D11DeviceContext context, bool encodePq, float nits = 200f)
    {
        if (!Available) return;

        ImGui.Render();
        var data = ImGui.GetDrawData();
        if (data.CmdListsCount == 0 || data.TotalVtxCount == 0) { RestoreState(context); return; }

        GrowBuffers(data.TotalVtxCount, data.TotalIdxCount);
        UploadGeometry(context, data);
        SetRenderState(context, data, encodePq, nits);
        SubmitDrawLists(context, data);
        RestoreState(context);
    }

    private unsafe void GrowBuffers(int vertexCount, int indexCount)
    {
        if (_vertexBuffer is null || _vertexCapacity < vertexCount)
        {
            _vertexBuffer?.Dispose();
            // Headroom so a UI that grows by one widget does not reallocate every frame.
            _vertexCapacity = vertexCount + 5000;
            _vertexBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(_vertexCapacity * sizeof(ImDrawVert)),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
        }

        if (_indexBuffer is null || _indexCapacity < indexCount)
        {
            _indexBuffer?.Dispose();
            _indexCapacity = indexCount + 10000;
            _indexBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(_indexCapacity * sizeof(ushort)),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.IndexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
        }
    }

    private unsafe void UploadGeometry(ID3D11DeviceContext context, ImDrawDataPtr data)
    {
        var vtx = context.Map(_vertexBuffer!, 0, MapMode.WriteDiscard);
        var idx = context.Map(_indexBuffer!, 0, MapMode.WriteDiscard);

        var vtxDst = (ImDrawVert*)vtx.DataPointer;
        var idxDst = (ushort*)idx.DataPointer;

        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            int vBytes = list.VtxBuffer.Size * sizeof(ImDrawVert);
            int iBytes = list.IdxBuffer.Size * sizeof(ushort);
            Unsafe.CopyBlock(vtxDst, list.VtxBuffer.Data.ToPointer(), (uint)vBytes);
            Unsafe.CopyBlock(idxDst, list.IdxBuffer.Data.ToPointer(), (uint)iBytes);
            vtxDst += list.VtxBuffer.Size;
            idxDst += list.IdxBuffer.Size;
        }

        context.Unmap(_vertexBuffer!, 0);
        context.Unmap(_indexBuffer!, 0);
    }

    private const int ConstantBytes = 80; // 4x4 matrix + one uint + one float, padded to 16

    private unsafe void SetRenderState(ID3D11DeviceContext context, ImDrawDataPtr data, bool encodePq, float nits)
    {
        // Orthographic projection straight from the draw data's own clip rectangle, so a
        // resize mid-frame cannot put the UI somewhere the input code disagrees with.
        float l = data.DisplayPos.X, r = l + data.DisplaySize.X;
        float t = data.DisplayPos.Y, b = t + data.DisplaySize.Y;

        var mapped = context.Map(_constants, 0, MapMode.WriteDiscard);
        var p = (float*)mapped.DataPointer;
        p[0] = 2f / (r - l); p[1] = 0f;             p[2] = 0f; p[3] = 0f;
        p[4] = 0f;           p[5] = 2f / (t - b);   p[6] = 0f; p[7] = 0f;
        p[8] = 0f;           p[9] = 0f;             p[10] = 0.5f; p[11] = 0f;
        p[12] = (r + l) / (l - r); p[13] = (t + b) / (b - t); p[14] = 0.5f; p[15] = 1f;
        ((uint*)p)[16] = encodePq ? 1u : 0u;
        p[17] = nits;
        context.Unmap(_constants, 0);

        context.IASetInputLayout(_layout);
        context.IASetVertexBuffer(0, _vertexBuffer!, (uint)sizeof(ImDrawVert));
        context.IASetIndexBuffer(_indexBuffer!, Format.R16_UInt, 0);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        context.VSSetShader(_vs);
        context.VSSetConstantBuffer(0, _constants);
        context.PSSetShader(_ps);
        context.PSSetConstantBuffer(0, _constants);
        context.PSSetSampler(0, _sampler);
        context.GSSetShader(null);
        context.HSSetShader(null);
        context.DSSetShader(null);

        context.OMSetBlendState(_blend, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
        context.OMSetDepthStencilState(_depth);
        context.RSSetState(_raster);
        context.RSSetViewport(0, 0, data.DisplaySize.X, data.DisplaySize.Y);
    }

    private void SubmitDrawLists(ID3D11DeviceContext context, ImDrawDataPtr data)
    {
        int vertexOffset = 0, indexOffset = 0;
        var clipOff = data.DisplayPos;

        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            for (int c = 0; c < list.CmdBuffer.Size; c++)
            {
                var cmd = list.CmdBuffer[c];
                if (cmd.UserCallback != IntPtr.Zero) continue; // we never install callbacks

                float x1 = cmd.ClipRect.X - clipOff.X, y1 = cmd.ClipRect.Y - clipOff.Y;
                float x2 = cmd.ClipRect.Z - clipOff.X, y2 = cmd.ClipRect.W - clipOff.Y;
                if (x2 <= x1 || y2 <= y1) continue;

                context.RSSetScissorRect((int)x1, (int)y1, (int)(x2 - x1), (int)(y2 - y1));
                context.PSSetShaderResource(0, new ID3D11ShaderResourceView(cmd.GetTexID()));
                context.DrawIndexed(cmd.ElemCount, cmd.IdxOffset + (uint)indexOffset,
                                    (int)cmd.VtxOffset + vertexOffset);
            }
            vertexOffset += list.VtxBuffer.Size;
            indexOffset += list.IdxBuffer.Size;
        }
    }

    /// <summary>
    /// Puts back the pipeline state the video path assumes. Scissor is the one that bites:
    /// left enabled with a stale rect, the next frame's video draw is silently clipped to
    /// wherever a UI panel happened to be.
    /// </summary>
    private static void RestoreState(ID3D11DeviceContext context)
    {
        context.RSSetState(null);
        context.OMSetBlendState(null);
        context.OMSetDepthStencilState(null);
        context.IASetInputLayout(null);
        context.IASetIndexBuffer(null, Format.Unknown, 0);
    }

    public void Dispose()
    {
        // First, and before anything is freed. DestroyWindow later sends WM_DESTROY, the
        // window procedure runs again, and anything it asks ImGui after this point would
        // be reading a context that no longer exists - which fails hard, not gracefully.
        Available = false;

        Fonts.Dispose();
        _indexBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _fontView?.Dispose();
        _fontTexture?.Dispose();
        _depth?.Dispose();
        _raster?.Dispose();
        _blend?.Dispose();
        _sampler?.Dispose();
        _constants?.Dispose();
        _layout?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        if (_context != IntPtr.Zero) { ImGui.DestroyContext(_context); _context = IntPtr.Zero; }
    }

    // NOTE: keep this shader pure ASCII. Non-ASCII bytes truncate the source, which shows
    // up as a bogus "unexpected end of file" from the compiler.
    private const string ShaderSource = """
        cbuffer Params : register(b0)
        {
            float4x4 Projection;
            uint  EncodePq;   // 1 when the swapchain is presenting PQ / BT.2020
            float Nits;       // level the UI is re-encoded at when it is
            uint2 _pad;
        };

        Texture2D    Tex  : register(t0);
        SamplerState Samp : register(s0);

        struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
        struct VSOut { float4 pos : SV_Position; float4 col : COLOR0; float2 uv : TEXCOORD0; };

        VSOut VS(VSIn i)
        {
            VSOut o;
            o.pos = mul(Projection, float4(i.pos, 0.0, 1.0));
            o.col = i.col;
            o.uv  = i.uv;
            return o;
        }

        // SMPTE ST 2084. Input is absolute luminance normalised to 10000 nits.
        float3 LinearToPQ(float3 L)
        {
            const float m1 = 0.1593017578125, m2 = 78.84375;
            const float c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
            float3 Lm = pow(saturate(L), m1);
            return pow((c1 + c2 * Lm) / (1.0 + c3 * Lm), m2);
        }

        float4 PS(VSOut i) : SV_Target
        {
            float4 c = i.col * Tex.Sample(Samp, i.uv);

            // The UI is authored in sRGB. Written straight into a PQ swapchain those
            // values read as thousands of nits, which on an OLED is both unreadable and
            // a genuinely bad idea, so they are re-encoded at a comfortable level.
            if (EncodePq != 0) c.rgb = LinearToPQ(pow(c.rgb, 2.2) * (Nits / 10000.0));

            return c;
        }
        """;
}
