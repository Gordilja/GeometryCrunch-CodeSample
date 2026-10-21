using UnityEngine;

/// Liquid inside the cube. HP sets the fill level; a pendulum tilt driven by acceleration (not speed) plus a small
/// heightfield wave simulation give the movement. Both feed the LiquidHP body shader (the waterline on the glass walls)
/// and, when surfaceMaterial is set, a grid mesh that draws the liquid's actual surface.
/// View only: nothing here touches the simulation.
public class LiquidSlosh : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Renderer cubeRenderer;
    [SerializeField] private Renderer faceCap;
    [Tooltip("Material for the wave surface mesh (Custom/LiquidSurface). Leave empty to draw only the waterline on the walls.")]
    [SerializeField] private Material surfaceMaterial;

    [Header("Fill")]
    [Tooltip("Fill level before any HP update (the menu cube uses 0.5 so the liquid surface is visible)")]
    [Range(0f, 1f)] public float initialFill = 1f;
    public float hpLerpSpeed = 3f;
    [Tooltip("Liquid layer (object space) kept on the floor while HP > 0, so the bottom of the cube never shows.")]
    public float minLayer = 0.05f;
    [Tooltip("Highest point a wave or tilt may reach (object space; the lid is at 0.5).")]
    public float maxSurface = 0.47f;

    [Header("Slosh (tilt)")]
    [Tooltip("Sloshes per second at about two-thirds full. Shallow liquid sloshes slower, like real water.")]
    public float sloshFrequency = 2.4f;
    [Tooltip("0 = rocks forever, 1 = no rocking. Around 0.1 rocks 2-3 times and settles.")]
    [Range(0f, 1f)] public float sloshDamping = 0.12f;
    [Tooltip("Surface slope per unit of acceleration (world units/s²). Speeding up or slowing down tilts the liquid; constant speed does not.")]
    public float accelInfluence = 0.004f;
    public float maxSlope = 0.7f;

    [Header("Waves")]
    [Tooltip("How fast ripples cross the cube (object units/s) at about two-thirds full.")]
    public float waveSpeed = 2.5f;
    [Tooltip("How quickly ripples die out (1/s).")]
    public float waveDamping = 2.2f;
    [Tooltip("How hard the walls push ripples when the cube speeds up, slows down or turns.")]
    public float wallKick = 0.1f;
    [Tooltip("Exaggerates ripple slopes for lighting only (heights stay physical), so small ripples still catch the light.")]
    public float rippleNormalBoost = 4f;
    [Tooltip("Accelerations above this (world units/s²) are clamped; larger jumps are treated as teleports.")]
    public float maxAccel = 300f;

    private const string HpProperty = "_HPPercent";
    private const string CapColorProperty = "_Color";
    private const string FeatureColorProperty = "_FeatureColor";

    private const int GridSize = 12;
    private const float Step = 1f / 120f;

    private static readonly int FillLevelId = Shader.PropertyToID("_FillLevel");
    private static readonly int SurfaceSlopeId = Shader.PropertyToID("_SurfaceSlope");
    private static readonly int WaveTexId = Shader.PropertyToID("_WaveTex");
    private static readonly int WaveResId = Shader.PropertyToID("_WaveRes");
    private static readonly int DrivenFillId = Shader.PropertyToID("_DrivenFill");
    private static readonly int HpId = Shader.PropertyToID(HpProperty);

    private Material _mat;
    private Material _faceMat;
    private Transform _liquidTransform;
    private Color _defaultCapColor;

    private float _fillBottom = -0.5f;
    private float _fillTop = 0.4f;
    private float _targetHP = 1f;
    private float _currentHP = 1f;

    // Tilt pendulum (surface slope along local x / z)
    private Vector2 _slope, _slopeVel;

    // Wave heightfield (heights relative to the mean surface; mean kept at 0 so volume is conserved)
    private readonly float[] _height = new float[GridSize * GridSize];
    private readonly float[] _velocity = new float[GridSize * GridSize];
    private readonly float[] _output = new float[GridSize * GridSize];
    private Texture2D _waveTex;

    // Motion tracking (from the transform, so it also works for the menu cube)
    private Vector3 _prevPos;
    private Vector2 _smoothVel, _prevSmoothVel;
    private bool _hasPrev;
    private float _accumulator;

    // Surface mesh
    private Mesh _surfaceMesh;
    private Vector3[] _surfaceVerts;
    private Material _surfaceMat;

    void Awake()
    {
        _mat = FindLiquidMaterial();
        _faceMat = faceCap.material;

        if (_faceMat.HasProperty(CapColorProperty))
            _defaultCapColor = _faceMat.GetColor(CapColorProperty);

        _targetHP = _currentHP = initialFill;

        if (_mat.HasProperty("_FillBottom")) _fillBottom = _mat.GetFloat("_FillBottom");
        if (_mat.HasProperty("_FillTop")) _fillTop = _mat.GetFloat("_FillTop");

        _waveTex = new Texture2D(GridSize, GridSize, TextureFormat.RFloat, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        _mat.SetTexture(WaveTexId, _waveTex);
        _mat.SetFloat(WaveResId, GridSize);
        _mat.SetFloat(DrivenFillId, 1f);

        if (surfaceMaterial != null)
            CreateSurface();

        Apply();
    }

    void OnEnable()
    {
        // Hidden while dead / pooled: don't treat the time away (or a respawn teleport) as motion
        _hasPrev = false;
    }

    void OnDestroy()
    {
        if (_waveTex != null) Destroy(_waveTex);
        if (_surfaceMesh != null) Destroy(_surfaceMesh);
        if (_surfaceMat != null) Destroy(_surfaceMat);
    }

    // The LiquidHP material: on cubeRenderer if wired correctly, otherwise the first child renderer that has it
    // (e.g. PlayerVisualMenu.prefab has cubeRenderer pointing at the glass overlay).
    private Material FindLiquidMaterial()
    {
        foreach (var material in cubeRenderer.materials)
        {
            if (material.HasProperty(HpProperty))
            {
                _liquidTransform = cubeRenderer.transform;
                return material;
            }
        }

        foreach (var childRenderer in GetComponentsInChildren<Renderer>(true))
        {
            if (childRenderer == cubeRenderer || childRenderer == faceCap)
                continue;

            if (childRenderer.sharedMaterial != null && childRenderer.sharedMaterial.HasProperty(HpProperty))
            {
                _liquidTransform = childRenderer.transform;
                return childRenderer.material;
            }
        }

        _liquidTransform = cubeRenderer.transform;
        return cubeRenderer.material;
    }

    /// Recolours the face cap and its features (e.g. level enemy archetype). The liquid keeps its colours.
    public void SetFaceColors(Color capColor, Color featureColor)
    {
        if (!_faceMat.HasProperty(FeatureColorProperty)) return;
        _faceMat.SetColor(CapColorProperty, capColor);
        featureColor.a = 1f;
        _faceMat.SetColor(FeatureColorProperty, featureColor);
    }

    /// Back to the material's original face colours (entity views can be pooled and reused).
    public void ClearFaceColors()
    {
        if (!_faceMat.HasProperty(FeatureColorProperty)) return;
        _faceMat.SetColor(CapColorProperty, _defaultCapColor);
        _faceMat.SetColor(FeatureColorProperty, Color.clear);
    }

    public void SetHP(float current, float max)
    {
        _targetHP = max > 0f ? Mathf.Clamp01(current / max) : 0f;
    }

    public void SetFace(Texture faceTexture)
    {
        _faceMat.SetTexture("_MainTex", faceTexture);
    }

    /// Pushes the surface down at a point (local x/z in -0.5..0.5); the dip spreads out as ripples.
    public void Splash(Vector2 localXZ, float strength, float radius = 0.18f)
    {
        float invR2 = 1f / (radius * radius);
        for (int z = 0; z < GridSize; z++)
        for (int x = 0; x < GridSize; x++)
        {
            Vector2 p = GridPoint(x, z) - localXZ;
            _velocity[z * GridSize + x] -= strength * Mathf.Exp(-p.sqrMagnitude * invR2);
        }
    }

    /// Splash at a random spot near the walls (a hit from an unknown direction).
    public void SplashRandom(float strength)
    {
        float angle = Random.value * Mathf.PI * 2f;
        Splash(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 0.3f, strength);
    }

    void Update()
    {
        Tick(Time.deltaTime);
    }

    /// Advances the liquid by dt seconds. Public so editor previews can drive it without Play mode.
    public void Tick(float dt)
    {
        if (dt <= 0f) return;

        _currentHP = Mathf.MoveTowards(_currentHP, _targetHP, Mathf.Max(0.02f, Mathf.Abs(_targetHP - _currentHP)) * hpLerpSpeed * dt);

        Vector2 accel = SampleAcceleration(dt);

        _accumulator = Mathf.Min(_accumulator + dt, Step * 8f);
        while (_accumulator >= Step)
        {
            StepSimulation(accel);
            _accumulator -= Step;
        }

        Apply();
    }

    private Vector2 SampleAcceleration(float dt)
    {
        Vector3 pos = _liquidTransform.position;
        if (!_hasPrev || (pos - _prevPos).sqrMagnitude > 4f)
        {
            // First frame or a teleport (respawn): no motion
            _hasPrev = true;
            _prevPos = pos;
            _smoothVel = _prevSmoothVel = Vector2.zero;
            return Vector2.zero;
        }

        Vector3 delta = pos - _prevPos;
        _prevPos = pos;

        // Light smoothing: the view position steps with the 60 Hz simulation, so raw derivatives are spiky
        Vector2 vel = new Vector2(delta.x, delta.z) / dt;
        _smoothVel = Vector2.Lerp(_smoothVel, vel, 1f - Mathf.Exp(-dt / 0.03f));
        Vector2 accelWorld = Vector2.ClampMagnitude((_smoothVel - _prevSmoothVel) / dt, maxAccel);
        _prevSmoothVel = _smoothVel;

        Vector3 local = _liquidTransform.InverseTransformDirection(new Vector3(accelWorld.x, 0f, accelWorld.y));
        return new Vector2(local.x, local.z);
    }

    private void StepSimulation(Vector2 accel)
    {
        float fill = FillLevel();
        float depth = Mathf.Max(fill - _fillBottom, 0.01f);

        // Tilt pendulum. Frequency follows the first slosh mode of a box: w² ∝ tanh(π·depth), so shallow liquid
        // rocks slower. Scaled so sloshFrequency is hit at depth 0.6.
        float w0 = 2f * Mathf.PI * sloshFrequency;
        float w2 = w0 * w0 * Tanh(Mathf.PI * depth) / Tanh(Mathf.PI * 0.6f);
        float w = Mathf.Sqrt(w2);

        Vector2 target = -accel * accelInfluence;
        Vector2 slopeAccel = w2 * (target - _slope) - 2f * sloshDamping * w * _slopeVel;
        _slopeVel += slopeAccel * Step;
        _slope += _slopeVel * Step;
        if (_slope.sqrMagnitude > maxSlope * maxSlope)
        {
            _slope = _slope.normalized * maxSlope;
            _slopeVel *= 0.5f;
        }

        // Ripples: damped wave equation on the grid, walls reflect (mirrored neighbours)
        float dx = 1f / (GridSize - 1);
        float c = waveSpeed * Mathf.Sqrt(depth / 0.6f);
        c = Mathf.Min(c, 0.5f * dx / Step); // keep the integration stable
        float c2 = c * c / (dx * dx);

        for (int z = 0; z < GridSize; z++)
        for (int x = 0; x < GridSize; x++)
        {
            int i = z * GridSize + x;
            float h = _height[i];
            float lap = _height[z * GridSize + Mirror(x - 1)] + _height[z * GridSize + Mirror(x + 1)]
                      + _height[Mirror(z - 1) * GridSize + x] + _height[Mirror(z + 1) * GridSize + x] - 4f * h;

            float force = c2 * lap - waveDamping * _velocity[i];

            // Walls shove the liquid when the cube speeds up / slows down
            bool edge = x == 0 || z == 0 || x == GridSize - 1 || z == GridSize - 1;
            if (edge)
            {
                Vector2 p = GridPoint(x, z);
                force -= (accel.x * p.x + accel.y * p.y) * wallKick;
            }

            _velocity[i] += force * Step;
        }

        float mean = 0f;
        for (int i = 0; i < _height.Length; i++)
        {
            _height[i] += _velocity[i] * Step;
            mean += _height[i];
        }

        // Volume conservation: the ripples never add or remove liquid
        mean /= _height.Length;
        for (int i = 0; i < _height.Length; i++)
            _height[i] -= mean;
    }

    private void Apply()
    {
        float fill = FillLevel();

        // Squash tilt + ripples so the surface never dips below the floor layer or rises past the lid
        float maxDev = 0f, minDev = 0f;
        for (int z = 0; z < GridSize; z++)
        for (int x = 0; x < GridSize; x++)
        {
            Vector2 p = GridPoint(x, z);
            float dev = _slope.x * p.x + _slope.y * p.y + _height[z * GridSize + x];
            if (dev > maxDev) maxDev = dev;
            if (dev < minDev) minDev = dev;
        }

        float k = 1f;
        if (maxDev > 1e-5f) k = Mathf.Min(k, Mathf.Max(0f, maxSurface - fill) / maxDev);
        if (minDev < -1e-5f) k = Mathf.Min(k, Mathf.Max(0f, fill - (_fillBottom + 0.01f)) / -minDev);

        for (int i = 0; i < _output.Length; i++)
            _output[i] = _height[i] * k;
        _waveTex.SetPixelData(_output, 0);
        _waveTex.Apply(false);

        Vector2 slope = _slope * k;
        _mat.SetFloat(HpId, _currentHP);
        _mat.SetFloat(FillLevelId, fill);
        _mat.SetVector(SurfaceSlopeId, new Vector4(slope.x, slope.y, 0f, 0f));

        if (_surfaceMesh != null)
            UpdateSurface(fill, slope);
    }

    private float FillLevel()
    {
        if (_currentHP <= 0.001f) return _fillBottom;
        return Mathf.Lerp(_fillBottom + minLayer, _fillTop, _currentHP);
    }

    private void CreateSurface()
    {
        var go = new GameObject("LiquidSurface");
        go.layer = _liquidTransform.gameObject.layer;
        go.transform.SetParent(_liquidTransform, false);

        _surfaceVerts = new Vector3[GridSize * GridSize];
        var uvs = new Vector2[GridSize * GridSize];
        for (int z = 0; z < GridSize; z++)
        for (int x = 0; x < GridSize; x++)
        {
            int i = z * GridSize + x;
            Vector2 p = GridPoint(x, z) * 0.994f; // just inside the glass walls
            _surfaceVerts[i] = new Vector3(p.x, 0f, p.y);
            uvs[i] = new Vector2(x / (float)(GridSize - 1), z / (float)(GridSize - 1));
        }

        var tris = new int[(GridSize - 1) * (GridSize - 1) * 6];
        int t = 0;
        for (int z = 0; z < GridSize - 1; z++)
        for (int x = 0; x < GridSize - 1; x++)
        {
            int i = z * GridSize + x;
            tris[t++] = i; tris[t++] = i + GridSize; tris[t++] = i + 1;
            tris[t++] = i + 1; tris[t++] = i + GridSize; tris[t++] = i + GridSize + 1;
        }

        _surfaceMesh = new Mesh { name = "LiquidSurface" };
        _surfaceMesh.MarkDynamic();
        _surfaceMesh.vertices = _surfaceVerts;
        _surfaceMesh.uv = uvs;
        _surfaceMesh.triangles = tris;
        _surfaceMesh.bounds = new Bounds(Vector3.zero, Vector3.one);

        go.AddComponent<MeshFilter>().sharedMesh = _surfaceMesh;
        var renderer = go.AddComponent<MeshRenderer>();
        _surfaceMat = new Material(surfaceMaterial);
        if (_mat.HasProperty("_FoamColor"))
        {
            // Meniscus rim matches the waterline foam on the walls
            var foam = _mat.GetColor("_FoamColor");
            foam.a = _surfaceMat.GetColor("_EdgeColor").a;
            _surfaceMat.SetColor("_EdgeColor", foam);
        }
        renderer.sharedMaterial = _surfaceMat;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private void UpdateSurface(float fill, Vector2 slope)
    {
        bool empty = _currentHP <= 0.001f;
        _surfaceMesh.bounds = new Bounds(Vector3.zero, empty ? Vector3.zero : Vector3.one);

        for (int z = 0; z < GridSize; z++)
        for (int x = 0; x < GridSize; x++)
        {
            int i = z * GridSize + x;
            Vector2 p = GridPoint(x, z);
            _surfaceVerts[i].y = fill + slope.x * p.x + slope.y * p.y + _output[i];
        }

        _surfaceMesh.vertices = _surfaceVerts;
        _surfaceMesh.normals = ComputeNormals(slope);

        // Same colours as the body so the surface and the walls read as one liquid
        _surfaceMat.SetFloat(HpId, _currentHP);
        _surfaceMat.SetColor("_ColorFull", _mat.GetColor("_ColorFull"));
        _surfaceMat.SetColor("_ColorEmpty", _mat.GetColor("_ColorEmpty"));
    }

    private Vector3[] _surfaceNormals;

    // Central differences: the tilt part stays physical, the ripple part is boosted so it reads under the glow
    private Vector3[] ComputeNormals(Vector2 slope)
    {
        if (_surfaceNormals == null) _surfaceNormals = new Vector3[GridSize * GridSize];
        float inv2dx = (GridSize - 1) * 0.5f;
        for (int z = 0; z < GridSize; z++)
        for (int x = 0; x < GridSize; x++)
        {
            int xl = Mathf.Max(x - 1, 0), xr = Mathf.Min(x + 1, GridSize - 1);
            int zd = Mathf.Max(z - 1, 0), zu = Mathf.Min(z + 1, GridSize - 1);
            float dhdx = (_output[z * GridSize + xr] - _output[z * GridSize + xl]) * inv2dx * (2f / (xr - xl));
            float dhdz = (_output[zu * GridSize + x] - _output[zd * GridSize + x]) * inv2dx * (2f / (zu - zd));
            _surfaceNormals[z * GridSize + x] = new Vector3(-(slope.x + dhdx * rippleNormalBoost), 1f, -(slope.y + dhdz * rippleNormalBoost)).normalized;
        }
        return _surfaceNormals;
    }

    private static Vector2 GridPoint(int x, int z)
    {
        return new Vector2(x / (float)(GridSize - 1) - 0.5f, z / (float)(GridSize - 1) - 0.5f);
    }

    private static int Mirror(int i)
    {
        if (i < 0) return 1;
        if (i >= GridSize) return GridSize - 2;
        return i;
    }

    private static float Tanh(float x)
    {
        float e = Mathf.Exp(2f * x);
        return (e - 1f) / (e + 1f);
    }
}