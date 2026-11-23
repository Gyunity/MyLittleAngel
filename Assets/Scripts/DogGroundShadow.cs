using System.Collections.Generic;
using UnityEngine;

// 강아지 발밑에 블롭 그림자를 만든다.
// - 몸 아래 타원 두 겹: 넓고 옅은 그림자 + 좁고 진한 그림자. 모델 크기에 맞춰 자동으로 크기를 정하고 강아지와 함께 돈다
// - (선택) 발마다 작은 블롭: 발 본을 따라가고, 발이 들리면 옅어지면서 커진다
// 그림자는 항상 바닥 높이(groundRoot의 y)에 그려지므로 발에 붙어 보인다.
// DogRoot에 붙이는 것을 권장 (DogRoot가 꺼지면 그림자도 같이 꺼짐).
public class DogGroundShadow : MonoBehaviour
{
    [SerializeField] GlbRuntimeLoader dog;
    [Tooltip("바닥 높이 기준. 비워두면 이 컴포넌트가 붙은 Transform (DogRoot에 붙였다면 DogRoot)")]
    [SerializeField] Transform groundRoot;
    [Tooltip("MyLittleAngel/BlobShadow 셰이더. 빌드에 포함되도록 반드시 연결")]
    [SerializeField] Shader blobShader;
    [SerializeField] Color shadowColor = Color.black;
    [Tooltip("바닥과 겹쳐 깜빡이지 않도록 띄우는 높이 (m)")]
    [SerializeField] float heightAboveGround = 0.001f;

    [Header("몸 아래 넓은 그림자 (주변이 은은하게 어두워지는 느낌)")]
    [SerializeField, Range(0f, 1f)] float ambientOpacity = 0.35f;
    [SerializeField, Range(0f, 0.99f)] float ambientHardness = 0f;
    [Tooltip("모델 바운즈 대비 크기 (x: 좌우, y: 앞뒤). 몸보다 살짝 커야 떠 보이지 않음")]
    [SerializeField] Vector2 ambientSizeScale = new(1.4f, 1.15f);

    [Header("몸 바로 아래 진한 그림자 (바닥에 닿은 느낌)")]
    [SerializeField, Range(0f, 1f)] float coreOpacity = 0.45f;
    [SerializeField, Range(0f, 0.99f)] float coreHardness = 0.3f;
    [SerializeField] Vector2 coreSizeScale = new(0.8f, 0.7f);

    [Header("발 블롭 (선택)")]
    [Tooltip("발마다 작은 그림자를 추가. 끄면 몸 아래 그림자만 사용")]
    [SerializeField] bool useFootBlobs;
    [Tooltip("비워두면 이름에 leg/foot/paw가 들어간 본 체인의 끝 본을 자동으로 찾음")]
    [SerializeField] string[] footBoneNames = new string[0];
    [SerializeField, Range(0f, 1f)] float footOpacity = 0.3f;
    [SerializeField, Range(0f, 0.99f)] float footHardness = 0.25f;
    [Tooltip("발 블롭 지름 / 강아지 키")]
    [SerializeField] float footSizeRatio = 0.14f;
    [Tooltip("발 본 끝(발끝) 추정 길이 = 부모 본까지 거리 x 이 값. 0이면 본 위치 그대로")]
    [SerializeField, Range(0f, 1.5f)] float footTipScale = 1f;
    [Tooltip("이 높이(강아지 키 대비)만큼 들리면 블롭이 완전히 사라짐")]
    [SerializeField] float footFadeHeightRatio = 0.06f;
    [Tooltip("발이 들렸을 때 최대 크기 배율")]
    [SerializeField] float footLiftedScale = 1.5f;

    [Header("실기기 조절 패널")]
    [Tooltip("화면 왼쪽 위에 그림자 값을 조절하는 슬라이더를 띄움. 값을 정한 뒤에는 끄고 빌드")]
    [SerializeField] bool showTuningPanel;

    [Header("디버그")]
    [SerializeField] bool logBones;

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int OpacityId = Shader.PropertyToID("_Opacity");
    static readonly int HardnessId = Shader.PropertyToID("_Hardness");

    class Foot
    {
        public Transform bone;
        public Renderer renderer;
        public float groundedHeight = float.MaxValue; // 지금까지 본 가장 낮은 발끝 높이 = 바닥에 닿은 높이
    }

    readonly List<Foot> feet = new();
    Transform container;
    Mesh quadMesh;
    Material material;
    MaterialPropertyBlock block;
    Renderer ambientBlob;
    Renderer coreBlob;
    Vector3 bodyCenter; // groundRoot 로컬
    Vector3 bodySize;   // groundRoot 로컬
    float dogHeight;
    bool initialized;

    void Awake()
    {
        if (dog == null) dog = GetComponentInChildren<GlbRuntimeLoader>(true);
        if (groundRoot == null) groundRoot = transform;
        if (dog != null) dog.Loaded += Init;
        else Debug.LogError("[GroundShadow] GlbRuntimeLoader를 찾지 못했습니다");
    }

    void Start()
    {
        // 컴포넌트가 로드 완료 뒤에 켜진 경우
        if (dog != null && dog.IsLoaded && !initialized) Init();
    }

    void OnDestroy()
    {
        if (dog != null) dog.Loaded -= Init;
        if (material != null) Destroy(material);
        if (quadMesh != null) Destroy(quadMesh);
    }

    void Init()
    {
        if (initialized) return;
        if (blobShader == null) blobShader = Shader.Find("MyLittleAngel/BlobShadow");
        if (blobShader == null)
        {
            Debug.LogError("[GroundShadow] BlobShadow 셰이더가 없습니다. 인스펙터에 연결하세요");
            return;
        }

        if (dog.LocalBounds.size.y <= 0f)
        {
            Debug.LogError("[GroundShadow] 강아지 바운즈가 비어 있습니다");
            return;
        }

        initialized = true;
        material = new Material(blobShader) { name = "BlobShadow (runtime)" };
        block = new MaterialPropertyBlock();
        quadMesh = BuildQuad();

        container = new GameObject("GroundShadow").transform;
        container.SetParent(groundRoot, false);

        CreateBodyBlobs();
        // 패널에서 켜고 끌 수 있도록 발 블롭은 항상 만들어 두고 표시만 전환
        CreateFootBlobs();
        ApplySettings();
    }

    void CreateBodyBlobs()
    {
        // 모델 바운즈(강아지 로컬)를 groundRoot 로컬 좌표로 옮겨서 앞뒤/좌우 크기를 구함
        var local = dog.LocalBounds;
        var dogT = dog.transform;
        bodyCenter = groundRoot.InverseTransformPoint(dogT.TransformPoint(local.center));
        var size = groundRoot.InverseTransformVector(dogT.TransformVector(local.size));
        bodySize = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
        dogHeight = bodySize.y;

        // 넓고 옅은 그림자 위에 좁고 진한 그림자를 겹쳐서 가운데로 갈수록 진해지게 함
        ambientBlob = CreateBlob("BodyAmbient");
        coreBlob = CreateBlob("BodyCore");
    }

    void CreateFootBlobs()
    {
        var bones = dog.GetComponentsInChildren<Transform>(true);
        if (logBones)
        {
            var names = new List<string>();
            foreach (var t in bones) names.Add(t.name);
            Debug.Log($"[GroundShadow] 본 목록 ({names.Count}): {string.Join(", ", names)}");
        }

        var footBones = footBoneNames != null && footBoneNames.Length > 0
            ? FindByName(bones, footBoneNames)
            : FindLegEnds(bones);

        if (footBones.Count == 0)
        {
            Debug.LogWarning("[GroundShadow] 발 본을 찾지 못했습니다. 발 블롭은 사용할 수 없습니다. 'logBones'를 켜서 본 이름을 확인하고 footBoneNames에 넣으세요");
            return;
        }

        var used = new List<string>();
        foreach (var bone in footBones)
        {
            feet.Add(new Foot { bone = bone, renderer = CreateBlob("Foot_" + bone.name) });
            used.Add(bone.name);
        }
        Debug.Log($"[GroundShadow] 발 본 {used.Count}개: {string.Join(", ", used)}");
    }

    static List<Transform> FindByName(Transform[] bones, string[] names)
    {
        var result = new List<Transform>();
        foreach (var name in names)
        {
            var found = System.Array.Find(bones, t => t.name == name);
            if (found != null) result.Add(found);
            else Debug.LogWarning($"[GroundShadow] 발 본 없음: {name}");
        }
        return result;
    }

    // 이름에 leg/foot/paw가 들어간 본 중, 같은 종류의 자식이 없는 끝 본
    static List<Transform> FindLegEnds(Transform[] bones)
    {
        var result = new List<Transform>();
        foreach (var t in bones)
        {
            if (!IsLegBone(t)) continue;
            bool hasLegChild = false;
            foreach (Transform child in t)
                if (IsLegBone(child)) { hasLegChild = true; break; }
            if (!hasLegChild) result.Add(t);
        }
        return result;
    }

    static bool IsLegBone(Transform t)
    {
        var n = t.name.ToLowerInvariant();
        return n.Contains("leg") || n.Contains("foot") || n.Contains("paw");
    }

    Renderer CreateBlob(string name)
    {
        var go = new GameObject(name);
        go.layer = groundRoot.gameObject.layer;
        go.transform.SetParent(container, false);
        go.AddComponent<MeshFilter>().sharedMesh = quadMesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        return r;
    }

    void SetBlobParams(Renderer r, float opacity, float hardness)
    {
        r.GetPropertyBlock(block);
        block.SetFloat(OpacityId, opacity);
        block.SetFloat(HardnessId, hardness);
        r.SetPropertyBlock(block);
    }

    // 현재 설정값을 그림자에 반영 (초기화, 인스펙터 변경, 조절 패널 변경 시)
    void ApplySettings()
    {
        if (!initialized) return;
        material.SetColor(ColorId, shadowColor);

        SetBlobParams(ambientBlob, ambientOpacity, ambientHardness);
        SetBlobParams(coreBlob, coreOpacity, coreHardness);
        var position = new Vector3(bodyCenter.x, heightAboveGround, bodyCenter.z);
        ambientBlob.transform.localPosition = position;
        ambientBlob.transform.localScale = new Vector3(bodySize.x * ambientSizeScale.x, 1f, bodySize.z * ambientSizeScale.y);
        coreBlob.transform.localPosition = position;
        coreBlob.transform.localScale = new Vector3(bodySize.x * coreSizeScale.x, 1f, bodySize.z * coreSizeScale.y);

        foreach (var foot in feet) foot.renderer.enabled = useFootBlobs;
    }

    void LateUpdate()
    {
        // 애니메이션과 이동이 끝난 뒤에 발 위치를 읽음
        if (!initialized || !useFootBlobs || feet.Count == 0) return;

        float groundY = groundRoot.position.y;
        float size = dogHeight * footSizeRatio;
        float fadeHeight = Mathf.Max(dogHeight * footFadeHeightRatio, 0.0001f);
        var rotation = groundRoot.rotation;

        foreach (var foot in feet)
        {
            var tip = FootTip(foot.bone);
            float height = tip.y - groundY;
            if (height < foot.groundedHeight) foot.groundedHeight = height;

            float lift = Mathf.Clamp01((height - foot.groundedHeight) / fadeHeight);
            var t = foot.renderer.transform;
            t.SetPositionAndRotation(new Vector3(tip.x, groundY + heightAboveGround, tip.z), rotation);
            t.localScale = Vector3.one * (size * Mathf.Lerp(1f, footLiftedScale, lift));
            SetBlobParams(foot.renderer, footOpacity * (1f - lift), footHardness);
        }
    }

    // 끝 본은 시작점만 있고 끝점이 없어서, 본 방향(+Y)으로 부모 본 길이만큼 늘려 발끝을 추정
    Vector3 FootTip(Transform bone)
    {
        if (footTipScale <= 0f || bone.parent == null) return bone.position;
        float length = Vector3.Distance(bone.position, bone.parent.position);
        return bone.position + bone.up * (length * footTipScale);
    }

    // 에디터 Play 중 인스펙터에서 값을 바꾸면 바로 반영
    void OnValidate() => ApplySettings();

    // ---------- 실기기 조절 패널 ----------
    // 씬 수정 없이 쓰기 위해 IMGUI로 그림. 기기 폰트 문제를 피하려고 화면 글자는 영어로 둠.

    const float PanelWidth = 250f;   // 가상 좌표 (짧은 변을 400으로 맞춘 기준)
    static Rect panelScreenRect;     // 실제 화면 좌표 (왼쪽 위 원점)
    static int panelFrame = -10;
    bool panelOpen;
    bool panelChanged;
    Vector2 panelScroll;

    // TapToPlace가 바닥 터치로 처리하기 전에 확인. screenPos는 Input System 좌표(왼쪽 아래 원점)
    public static bool IsPointerOverTuningPanel(Vector2 screenPos)
    {
        if (Time.frameCount - panelFrame > 2) return false; // 패널이 그려지지 않는 중
        return panelScreenRect.Contains(new Vector2(screenPos.x, Screen.height - screenPos.y));
    }

    void OnGUI()
    {
        if (!showTuningPanel || !initialized) return;

        float scale = Mathf.Min(Screen.width, Screen.height) / 400f;
        var safe = Screen.safeArea; // 왼쪽 아래 원점
        float left = safe.xMin / scale + 8f;
        float top = (Screen.height - safe.yMax) / scale + 8f;
        float maxHeight = safe.height / scale - 16f;

        var rect = panelOpen
            ? new Rect(left, top, PanelWidth, Mathf.Min(560f, maxHeight))
            : new Rect(left, top, 90f, 40f);

        var oldMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        GUILayout.BeginArea(rect, GUI.skin.box);

        if (GUILayout.Button(panelOpen ? "Close" : "Shadow", GUILayout.Height(28f))) panelOpen = !panelOpen;
        if (panelOpen) DrawPanelContents();

        GUILayout.EndArea();
        GUI.matrix = oldMatrix;

        panelScreenRect = new Rect(rect.x * scale, rect.y * scale, rect.width * scale, rect.height * scale);
        panelFrame = Time.frameCount;
    }

    void DrawPanelContents()
    {
        panelChanged = false;
        panelScroll = GUILayout.BeginScrollView(panelScroll);

        GUILayout.Label("-- Ambient (wide) --");
        ambientOpacity = Slider("Opacity", ambientOpacity, 0f, 1f);
        ambientHardness = Slider("Hardness", ambientHardness, 0f, 0.99f);
        ambientSizeScale.x = Slider("Width", ambientSizeScale.x, 0.3f, 2.5f);
        ambientSizeScale.y = Slider("Length", ambientSizeScale.y, 0.3f, 2.5f);

        GUILayout.Label("-- Core (under body) --");
        coreOpacity = Slider("Opacity", coreOpacity, 0f, 1f);
        coreHardness = Slider("Hardness", coreHardness, 0f, 0.99f);
        coreSizeScale.x = Slider("Width", coreSizeScale.x, 0.2f, 2f);
        coreSizeScale.y = Slider("Length", coreSizeScale.y, 0.2f, 2f);

        GUILayout.Label("-- Feet --");
        bool foot = GUILayout.Toggle(useFootBlobs, feet.Count > 0 ? " Use foot blobs" : " Use foot blobs (no bones)");
        if (foot != useFootBlobs) { useFootBlobs = foot; panelChanged = true; }
        if (useFootBlobs)
        {
            footOpacity = Slider("Opacity", footOpacity, 0f, 1f);
            footHardness = Slider("Hardness", footHardness, 0f, 0.99f);
            footSizeRatio = Slider("Size", footSizeRatio, 0.02f, 0.4f);
            footTipScale = Slider("Tip offset", footTipScale, 0f, 1.5f);
        }

        GUILayout.Space(6f);
        if (GUILayout.Button("Log values", GUILayout.Height(28f))) LogSettings();

        GUILayout.EndScrollView();
        if (panelChanged) ApplySettings();
    }

    float Slider(string label, float value, float min, float max)
    {
        GUILayout.Label($"{label}: {value:0.00}");
        float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Height(18f));
        if (!Mathf.Approximately(v, value)) panelChanged = true;
        return v;
    }

    // 패널에서 맞춘 값을 Logcat에서 확인해 인스펙터에 옮겨 적기 위함
    void LogSettings()
    {
        Debug.Log("[GroundShadow] 현재 값\n" +
            $"Ambient Opacity {ambientOpacity:0.00}, Hardness {ambientHardness:0.00}, Size Scale ({ambientSizeScale.x:0.00}, {ambientSizeScale.y:0.00})\n" +
            $"Core Opacity {coreOpacity:0.00}, Hardness {coreHardness:0.00}, Size Scale ({coreSizeScale.x:0.00}, {coreSizeScale.y:0.00})\n" +
            $"Use Foot Blobs {useFootBlobs}, Foot Opacity {footOpacity:0.00}, Hardness {footHardness:0.00}, Size Ratio {footSizeRatio:0.00}, Tip Scale {footTipScale:0.00}");
    }

    // 원점 중심, 바닥(XZ)에 눕힌 1x1 사각형
    static Mesh BuildQuad()
    {
        var mesh = new Mesh { name = "BlobShadowQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
            new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f),
        };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        mesh.RecalculateBounds();
        return mesh;
    }

    void OnDrawGizmosSelected()
    {
        if (!initialized || !useFootBlobs) return;
        Gizmos.color = Color.yellow;
        foreach (var foot in feet)
        {
            var tip = FootTip(foot.bone);
            Gizmos.DrawLine(foot.bone.position, tip);
            Gizmos.DrawWireSphere(tip, 0.005f);
        }
    }
}
