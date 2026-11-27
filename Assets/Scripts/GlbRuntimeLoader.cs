using GLTFast;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class GlbRuntimeLoader : MonoBehaviour
{
    [SerializeField] private string fileName = "SampleDog.glb";
    [Tooltip("어깨 높이 측정에 실패했을 때만 사용: 전체 renderer.bounds 높이 (m)")]
    [SerializeField] private float targetHeight = 0.5f;

    [Header("실제 크기 (어깨 높이)")]
    [Tooltip("강아지 키: 바닥에서 어깨(기갑)까지, cm. 0이면 기본값 사용. Play 중에 바꾸면 바로 반영")]
    [SerializeField, Min(0f)] private float shoulderHeightCm = 0f;
    [Tooltip("입력이 없을 때 쓰는 어깨 높이 (cm). 0이면 자동: 기존 크기(전체 bounds 0.5m로 맞추던 크기)일 때의 어깨 높이를 써서 크기가 바뀌지 않음")]
    [SerializeField, Min(0f)] private float defaultShoulderHeightCm = 0f;
    private float autoDefaultShoulderCm;  // 기존 크기일 때의 어깨 높이 (로드 시 계산)
    private const float MinShoulderCm = 10f;
    private const float MaxShoulderCm = 110f;

    [SerializeField, Range(0f, 1f)] private float normalStrength = 0.3f;
    [Tooltip("비워두면 이름에 'walk'가 들어간 클립, 없으면 첫 번째 클립을 사용")]
    [SerializeField] private string walkClipName = "";
    [Tooltip("비워두면 이름에 'idle'이 들어간 클립을 사용. 없으면 코드로 만든 idle 클립을 사용")]
    [SerializeField] private string idleClipName = "";
    [SerializeField] private float crossFadeTime = 0.2f;
    [Tooltip("도착 후 idle로 넘어가는 블렌드 시간")]
    [SerializeField] private float stopFadeTime = 0.4f;

    [Header("코드로 만드는 Idle (GLB에 idle 클립이 없을 때)")]
    [Tooltip("숨쉬기: 가슴이 앞뒤로 기울어지는 각도")]
    [SerializeField] private float idleBreathAngle = 1.5f;
    [Tooltip("고개를 좌우로 천천히 돌리는 각도")]
    [SerializeField] private float idleHeadYawAngle = 8f;
    [Tooltip("고개를 위아래로 끄덕이는 각도")]
    [SerializeField] private float idleHeadPitchAngle = 3f;
    [Tooltip("꼬리 마디 하나당 좌우 흔들림 각도")]
    [SerializeField] private float idleTailWagAngle = 6f;
    [Header("크기 측정 (디버그)")]
    [Tooltip("로드 직후 Renderer, 본, 어깨 높이, 전체 높이, XR Origin 스케일을 로그로 출력하고 기즈모로 그림")]
    [SerializeField] private bool logMeasurement = true;
    [Header("어깨 높이 측정")]
    [Tooltip("어깨 위치 기준 본 이름 (여러 개는 쉼표로 구분, 평균 위치 사용). 비우면 frontleg, R_frontleg (앞다리 뿌리)")]
    [SerializeField] private string shoulderBoneName = "";
    [Tooltip("어깨 단면 반폭 = 몸 길이 x 이 값")]
    [SerializeField, Range(0.005f, 0.1f)] private float shoulderSliceRatio = 0.025f;
    [Tooltip("단면 안에서 어깨 본보다 높은 정점(등 윗면)의 이 퍼센타일을 어깨 높이로 사용. 1이면 최대값. 낮출수록 튀는 털 끝을 더 많이 무시")]
    [SerializeField, Range(0.5f, 1f)] private float shoulderPercentile = 0.98f;
    [Tooltip("어깨 높이를 잴 때 뺄 본 이름 키워드 (쉼표 구분). 이 본과 그 자식 본에 붙은 정점은 제외")]
    [SerializeField] private string shoulderExcludeBones = "head,tail";
    [Tooltip("제외할 본 가중치 합이 이 값 이상인 정점은 어깨 측정에서 뺌")]
    [SerializeField, Range(0.05f, 1f)] private float shoulderExcludeMaxWeight = 0.5f;
    [Tooltip("등선을 뒷다리에서 앞쪽으로 따라가다 기울기(높이/길이)가 이 값을 처음 넘는 곳 = 목이 시작되는 곳. 그 직전 높이를 어깨로 봄. " +
             "1 = 45도, 0.5 = 약 27도. 초록 표시선이 목에 걸리면 낮추고, 등 중간에 걸리면 높임")]
    [SerializeField, Range(0.2f, 3f)] private float shoulderNeckSlope = 1f;
    [Tooltip("기기 화면에 측정한 어깨 위치를 초록 세로선으로 표시 (확인용, 값을 정한 뒤에는 끄기)")]
    [SerializeField] private bool showShoulderMarker = true;
    private LineRenderer shoulderMarker;
    private static readonly string[] DefaultShoulderBones = { "frontleg", "R_frontleg" };
    private static readonly string[] DefaultHipBones = { "backleg", "R_backleg" };
    private static readonly string[] FallbackShoulderKeywords = { "shoulder", "scapula", "clavicle", "withers", "chest" };
    private static readonly string[] CandidateBoneKeywords =
        { "shoulder", "scapula", "clavicle", "withers", "chest", "spine", "neck", "arm", "leg", "foot", "paw" };

    // 로드 직후 서 있는 자세로 한 번 잰 기준값. 크기를 바꿀 때는 이 값에 비율만 곱한다 (애니메이션 자세 영향 없음)
    private bool hasBase;
    private bool fallbackApplied;
    private Vector3 baseScale;
    private float baseShoulderHeight;   // baseScale일 때 어깨 높이 (부모 좌표, m)
    private Vector3 groundAnchorLocal;  // 몸 중앙 바로 아래 가장 낮은 정점 높이의 점 (이 Transform 로컬). 부모 원점에 맞춘다
    private Bounds baseLocalBounds;     // 서 있는 자세의 정점 범위 (이 Transform 로컬)
    private float appliedShoulderCm = -1f;
    private float oldFitBoundsHeight;   // 애니메이션 준비 전 renderer.bounds 높이 (기존 방식 비교용)

    // 기즈모용 마지막 측정 결과 (이 Transform 로컬 좌표)
    private bool hasMeasurement;
    private Vector3 gizmoGround, gizmoShoulder, gizmoTop;
    private Bounds gizmoSlice;
    private bool gizmoLengthIsX;
    private const string ProceduralIdleClip = "__ProceduralIdle";
    private const float IdleLength = 6f;   // 이 길이 안에서 모든 움직임이 정수 번 반복되어 끊김 없이 루프
    private const int IdleSamples = 60;
    private float appliedNormalStrength = -1f;
    private Animation anim;
    private string firstClip;
    private string walkClip;
    private string idleClip;
    private bool isLoaded;
    public event System.Action Loaded;
    public bool IsLoaded => isLoaded;
    // 크기 맞춘 직후 모델 바운즈 (이 Transform 로컬 좌표). 오브젝트가 꺼져 있어도 쓸 수 있도록 저장
    public Bounds LocalBounds { get; private set; }
    // 지금 적용된 어깨 높이 (cm)
    public float ShoulderHeightCm => appliedShoulderCm;
    public float DefaultShoulderHeightCm => defaultShoulderHeightCm > 0f ? defaultShoulderHeightCm : autoDefaultShoulderCm;
    // 기본 크기 대비 현재 크기 배율 (1 = 기본 크기). 어깨 측정에 실패해 기존 방식으로 맞춘 경우는 크기를 바꾸지 않으므로 1
    public float SizeRatio => hasBase && appliedShoulderCm > 0f && DefaultShoulderHeightCm > 0f
        ? appliedShoulderCm / DefaultShoulderHeightCm : 1f;
    // 크기(스케일, 위치, LocalBounds)가 바뀐 뒤 호출. 로드 중 첫 적용은 Loaded보다 먼저 온다
    public event System.Action SizeChanged;

    // 테스트/외부 입력용. 0 이하면 기본값. 다음 Update(오브젝트가 켜져 있을 때)에 반영
    public void SetShoulderHeightCm(float cm) => shoulderHeightCm = Mathf.Max(0f, cm);

    private float EffectiveShoulderCm =>
        shoulderHeightCm > 0f ? Mathf.Clamp(shoulderHeightCm, MinShoulderCm, MaxShoulderCm) : DefaultShoulderHeightCm;

    async void Start()
    {
        string path = Path.Combine(Application.persistentDataPath, fileName);
        Debug.Log($"[GlbLoader] 경로 : {path}");

        if (!File.Exists(path))
        {
            string src = Path.Combine(Application.streamingAssetsPath, fileName);
            if (!src.Contains("://")) src = "file://" + src;

            using var req = UnityEngine.Networking.UnityWebRequest.Get(src);
            await req.SendWebRequest();

            if(req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[GlbLoader] 복사 실패: {req.error}");
                return;
            }

            File.WriteAllBytes(path, req.downloadHandler.data);
        }

        var settings = new ImportSettings
        {
            AnimationMethod = AnimationMethod.Legacy
        };

        var gltf = new GltfImport();

        if (!await gltf.Load(path, settings))
        {
            Debug.LogError("[GlbLoader] GLB 로드 실패");
            return;
        }

        if (!await gltf.InstantiateMainSceneAsync(transform))
        {
            Debug.LogError("[GlbLoader] 생성 실패");
            return;
        }

        // 서 있는 자세로 재야 하므로 애니메이션 준비를 먼저 한다.
        // Loaded 구독자가 오브젝트를 비활성화할 수 있으므로 크기 맞추기까지 Loaded 전에 끝낸다
        // 기존 방식(FitToHeight)은 애니메이션 준비 전에 bounds를 쟀으므로, 기본값 비교용으로 같은 시점 값을 남긴다
        var preAnimRenderers = GetComponentsInChildren<Renderer>(true);
        if (preAnimRenderers.Length > 0) oldFitBoundsHeight = MeasureFrame.InverseTransformVector(
            new Vector3(0f, GetBounds(preAnimRenderers).size.y, 0f)).magnitude;
        SetupAnimation();
        if (logMeasurement) LogModelInfo();
        hasBase = MeasureBase();
        ApplySize();
        if (logMeasurement) MeasureStandingPose("적용 후 서 있는 자세 (걷기 첫 프레임)");
        isLoaded = true;
        Loaded?.Invoke();
    }

    private void SetupAnimation()
    {
        anim = GetComponentInChildren<Animation>(true);
        if (anim == null)
        {
            Debug.LogError("[GlbLoader] Animation 컴포넌트가 없습니다");
            return;
        }
        anim.playAutomatically = false;
        foreach (AnimationState state in anim)
        {
            Debug.Log($"[GlbLoader] 클립 : {state.name}");
            state.wrapMode = WrapMode.Loop;
            firstClip ??= state.name;
        }

        walkClip = ResolveClip(walkClipName, "walk") ?? firstClip;
        idleClip = ResolveClip(idleClipName, "idle");
        if (idleClip == walkClip) idleClip = null;
        // idle 클립이 없으면 걷기 첫 프레임 자세를 기준으로 idle 클립을 만든다
        if (idleClip == null && walkClip != null)
        {
            SnapToWalkFirstFrame();
            idleClip = BuildProceduralIdle();
        }
        Debug.Log($"[GlbLoader] 걷기 클립: {walkClip ?? "(없음)"}, 대기 클립: {idleClip ?? "(없음)"}");

        if (idleClip != null) anim.Play(idleClip);
        else SnapToWalkFirstFrame();
    }

    // 걷기 첫 프레임 자세 위에 숨쉬기, 고개 움직임, 꼬리 흔들기를 얹은 루프 클립을 만든다.
    // 걷기에서 블렌드될 때 굳는 뼈가 없도록 모든 Transform의 위치/회전/스케일을 포함한다.
    private string BuildProceduralIdle()
    {
        var root = anim.transform;
        var clip = new AnimationClip { legacy = true, name = ProceduralIdleClip, wrapMode = WrapMode.Loop };
        var rootInv = Quaternion.Inverse(root.rotation);
        var chestBones = new List<string>();
        var headBones = new List<string>();
        var tailBones = new List<string>();

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root) continue;
            string path = GetPath(root, t);
            SetConstantCurves(clip, path, "localPosition", t.localPosition);
            SetConstantCurves(clip, path, "localScale", t.localScale);

            var baseRot = t.localRotation;
            if (!HasIdleMotion(t.name))
            {
                SetConstantCurves(clip, path, baseRot);
                continue;
            }

            if (t.name == "chest") chestBones.Add(path);
            else if (t.name == "head") headBones.Add(path);
            else tailBones.Add(path);

            // 움직임은 강아지 기준 축(위 = Y, 오른쪽 = X, 앞 = Z)으로 정의하고 뼈의 로컬 회전으로 변환
            var parentRot = rootInv * t.parent.rotation;
            var parentInv = Quaternion.Inverse(parentRot);
            int depth = GetDepth(root, t);
            var x = new Keyframe[IdleSamples + 1];
            var y = new Keyframe[IdleSamples + 1];
            var z = new Keyframe[IdleSamples + 1];
            var w = new Keyframe[IdleSamples + 1];
            for (int i = 0; i <= IdleSamples; i++)
            {
                float time = IdleLength * i / IdleSamples;
                var q = parentInv * IdleMotion(t.name, depth, time) * parentRot * baseRot;
                x[i] = new Keyframe(time, q.x);
                y[i] = new Keyframe(time, q.y);
                z[i] = new Keyframe(time, q.z);
                w[i] = new Keyframe(time, q.w);
            }
            clip.SetCurve(path, typeof(Transform), "localRotation.x", new AnimationCurve(x));
            clip.SetCurve(path, typeof(Transform), "localRotation.y", new AnimationCurve(y));
            clip.SetCurve(path, typeof(Transform), "localRotation.z", new AnimationCurve(z));
            clip.SetCurve(path, typeof(Transform), "localRotation.w", new AnimationCurve(w));
        }
        clip.EnsureQuaternionContinuity();

        LogIdleBones("가슴", "chest", chestBones);
        LogIdleBones("머리", "head", headBones);
        LogIdleBones("꼬리", "tail*", tailBones);

        anim.AddClip(clip, ProceduralIdleClip);
        anim[ProceduralIdleClip].wrapMode = WrapMode.Loop;
        return ProceduralIdleClip;
    }

    private static void LogIdleBones(string part, string pattern, List<string> bones)
    {
        if (bones.Count > 0)
            Debug.Log($"[GlbLoader] Idle {part} 뼈 {bones.Count}개: {string.Join(", ", bones)}");
        else
            Debug.LogWarning($"[GlbLoader] Idle {part} 뼈를 찾지 못했습니다 (찾는 이름: {pattern}). 이 부위는 움직이지 않습니다");
    }

    private static bool HasIdleMotion(string boneName) =>
        boneName == "chest" || boneName == "head" ||
        boneName.StartsWith("tail", System.StringComparison.OrdinalIgnoreCase);

    private Quaternion IdleMotion(string boneName, int depth, float time)
    {
        float phase = 2f * Mathf.PI * time / IdleLength;
        if (boneName == "chest")   // 3초 주기 숨쉬기
            return Quaternion.Euler(idleBreathAngle * Mathf.Sin(2f * phase), 0f, 0f);
        if (boneName == "head")    // 6초 주기로 천천히 두리번
            return Quaternion.Euler(idleHeadPitchAngle * Mathf.Sin(2f * phase + 1f),
                                    idleHeadYawAngle * Mathf.Sin(phase), 0f);
        // 꼬리: 1.2초 주기, 끝으로 갈수록 살짝 늦게 따라오도록 위상 지연
        return Quaternion.Euler(0f, idleTailWagAngle * Mathf.Sin(5f * phase - depth * 0.4f), 0f);
    }

    private static void SetConstantCurves(AnimationClip clip, string path, string property, Vector3 v)
    {
        clip.SetCurve(path, typeof(Transform), property + ".x", AnimationCurve.Constant(0f, IdleLength, v.x));
        clip.SetCurve(path, typeof(Transform), property + ".y", AnimationCurve.Constant(0f, IdleLength, v.y));
        clip.SetCurve(path, typeof(Transform), property + ".z", AnimationCurve.Constant(0f, IdleLength, v.z));
    }

    private static void SetConstantCurves(AnimationClip clip, string path, Quaternion q)
    {
        clip.SetCurve(path, typeof(Transform), "localRotation.x", AnimationCurve.Constant(0f, IdleLength, q.x));
        clip.SetCurve(path, typeof(Transform), "localRotation.y", AnimationCurve.Constant(0f, IdleLength, q.y));
        clip.SetCurve(path, typeof(Transform), "localRotation.z", AnimationCurve.Constant(0f, IdleLength, q.z));
        clip.SetCurve(path, typeof(Transform), "localRotation.w", AnimationCurve.Constant(0f, IdleLength, q.w));
    }

    private static string GetPath(Transform root, Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != root; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    private static int GetDepth(Transform root, Transform t)
    {
        int depth = 0;
        for (var p = t.parent; p != root; p = p.parent) depth++;
        return depth;
    }

    private string ResolveClip(string clipName, string keyword)
    {
        if (!string.IsNullOrEmpty(clipName))
        {
            if (anim.GetClip(clipName) != null) return clipName;
            Debug.LogWarning($"[GlbLoader] 지정한 클립 없음: {clipName}");
        }
        foreach (AnimationState state in anim)
            if (state.name.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0)
                return state.name;
        return null;
    }

    public void PlayWalk()
    {
        if (anim == null || walkClip == null) return;
        anim[walkClip].speed = 1f;
        // IsPlaying은 idle로 넘어가며 사라지는 중인 걷기도 true라서 조건 없이 항상 CrossFade.
        // 이미 걷는 중이면 처음부터 다시 재생하지 않고 그대로 이어감
        anim.CrossFade(walkClip, crossFadeTime);
    }

    // 이동 속도에 맞춰 걷기 애니메이션 재생 속도를 조절 (감속할 때 발 미끄러짐 방지)
    public void SetWalkSpeed(float speed)
    {
        if (anim == null || walkClip == null) return;
        anim[walkClip].speed = speed;
    }

    public void StopWalk()
    {
        if (anim == null) return;
        if (idleClip != null)
        {
            // idle 처음(움직임 0)부터 시작해서 걷기에서 자연스럽게 넘어가도록
            if (!anim.IsPlaying(idleClip)) anim[idleClip].time = 0f;
            anim.CrossFade(idleClip, stopFadeTime);
            return;
        }
        anim.Stop();
    }

    // 배치 전에 꺼졌다가 다시 켜질 때 idle이 멈춰 있으면 다시 재생
    private void OnEnable()
    {
        if (anim != null && idleClip != null && !anim.isPlaying) anim.Play(idleClip);
    }

    // 걷기 클립 첫 프레임 자세를 즉시 적용 (로드 직후 초기 자세용)
    private void SnapToWalkFirstFrame()
    {
        if (walkClip == null) return;
        anim.Stop();
        var state = anim[walkClip];
        state.enabled = true;
        state.time = 0f;
        state.weight = 1f;
        anim.Sample();
        state.enabled = false;
    }
    private void Update()
    {
        if (isLoaded && !Mathf.Approximately(normalStrength, appliedNormalStrength))
            ApplyNormalStrength(normalStrength);
        // 인스펙터나 SetShoulderHeightCm으로 바뀐 키를 반영
        if (isLoaded && !Mathf.Approximately(EffectiveShoulderCm, appliedShoulderCm))
            ApplySize();
    }

    // 어깨 높이(cm)에 맞춰 스케일을 정하고, 가장 낮은 정점이 부모(DogRoot) 원점 높이에 오도록 놓는다
    private void ApplySize()
    {
        float cm = EffectiveShoulderCm;
        if (shoulderHeightCm > 0f && !Mathf.Approximately(cm, shoulderHeightCm))
            Debug.LogWarning($"[GlbLoader] 어깨 높이 {shoulderHeightCm}cm는 범위({MinShoulderCm}~{MaxShoulderCm}cm) 밖이라 {cm}cm로 맞춥니다");

        if (hasBase)
        {
            transform.localScale = baseScale * (cm / 100f / baseShoulderHeight);
            if (transform.parent != null)
                transform.localPosition = -(transform.localRotation * Vector3.Scale(transform.localScale, groundAnchorLocal));
            else
                Debug.LogWarning("[GlbLoader] 부모가 없어 바닥 위치를 맞추지 않습니다");
            Debug.Log($"[GlbLoader] 크기 적용: 어깨 {cm:0.0}cm ({(shoulderHeightCm > 0f ? "입력값" : "기본값")}), 스케일 {transform.localScale.x:0.#####}");
        }
        else if (!fallbackApplied)
        {
            Debug.LogWarning($"[GlbLoader] 어깨 높이를 재지 못해 기존 방식(전체 bounds {targetHeight}m)으로 맞춥니다. 어깨 높이 입력은 무시됩니다");
            FitToHeight(targetHeight);
            fallbackApplied = true;
        }
        appliedShoulderCm = cm;
        UpdateLocalBounds();
        SizeChanged?.Invoke();
    }

    // 어깨 측정에 실패했을 때만 쓰는 기존 방식
    private void FitToHeight(float height)
    {
        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        Bounds bounds = GetBounds(renderers);
        if (bounds.size.y <= 0f) return;

        transform.localScale *= height / bounds.size.y;

        bounds = GetBounds(renderers);
        Vector3 offset = transform.position - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        transform.position += offset;
    }

    // 그림자가 쓰는 바운즈 (이 Transform 로컬).
    // 기준 측정이 있으면 서 있는 자세의 실제 정점 범위를 쓴다. 로컬 좌표라 크기를 바꿔도 값은 그대로이고,
    // 받는 쪽에서 transform으로 변환하면 실제 크기가 된다
    private void UpdateLocalBounds()
    {
        if (hasBase)
        {
            LocalBounds = baseLocalBounds;
            return;
        }
        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;
        var bounds = GetBounds(renderers);
        var localSize = transform.InverseTransformVector(bounds.size);
        LocalBounds = new Bounds(transform.InverseTransformPoint(bounds.center),
            new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z)));
    }
    private void ApplyNormalStrength(float strength)
    {
        int count = 0;
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            foreach (var m in r.materials)
            {
                if (m.HasProperty("normalTexture_scale")) { m.SetFloat("normalTexture_scale", strength); count++; }
                if (m.HasProperty("_BumpScale")) { m.SetFloat("_BumpScale", strength); count++; }
            }
        }
        appliedNormalStrength = strength;
        Debug.Log($"[GlbLoader] 노멀 세기 {strength} 적용, 재질 {count}개");
    }
    // 모델 Renderer들의 bounds (디버그용 어깨 표시선은 제외)
    private Bounds GetBounds(Renderer[] renderers)
    {
        bool init = false;
        var bounds = new Bounds();
        foreach (var r in renderers)
        {
            if (r == shoulderMarker) continue;
            if (!init) { bounds = r.bounds; init = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return bounds;
    }

    private void OnDestroy()
    {
        if (shoulderMarker != null) Destroy(shoulderMarker.material);
    }

    // ---------- 크기 측정 ----------
    // 어깨 높이 = 앞다리 뿌리 위치의 몸 단면에서, 어깨 본보다 높은 정점(등 윗면)의 상위 퍼센타일 높이 - 가장 낮은 정점 높이

    private const string MeasureTag = "[GlbLoader][Measure]";

    // 측정 기준 좌표계: 부모(DogRoot). 스케일 1이면 단위가 m
    private Transform MeasureFrame => transform.parent != null ? transform.parent : transform;

    // 현재 자세의 정점으로 잰 결과 (모두 MeasureFrame 로컬)
    private struct SizeSample
    {
        public List<Vector3> points;
        public List<float> excluded;  // 정점별 머리/꼬리 본 가중치 합 (0 = 몸통, 1 = 머리나 꼬리)
        public Bounds vertexBounds;
        public float shoulderYNoExclude, shoulderYStrict; // 비교용: 머리/꼬리 제외 없이, 엄격하게(가중치 0.1 미만만)
        public bool lengthIsX;
        public float groundY, topY;
        public string anchorInfo;
        public bool hasAnchor;
        public Vector3 shoulderRef;   // 어깨 기준 본들의 평균 위치
        public float halfWidth;
        public int sliceCount;        // 단면 안 등 윗면 정점 수 (비교용 단면 방식)
        public float sliceShoulderY, sliceMaxY; // 비교용 단면 방식 결과
        public bool found;
        public string method;         // 어깨 높이를 정한 방식
        public float shoulderY;       // 최종 어깨 높이
        public Vector3 withersPoint;  // 어깨(기갑) 위치 (윗면 위의 점)
        public float hipAlong, frontAlong, binSize;
        public Bounds sliceBounds;    // 어깨로 잡은 구간의 몸통 정점 범위 (기즈모용)
        public float Along(Vector3 p) => lengthIsX ? p.x : p.z;
        public Vector3 AtAlong(float along, float y, Vector3 lateralRef) =>
            lengthIsX ? new Vector3(along, y, lateralRef.z) : new Vector3(lateralRef.x, y, along);
        public float BodyLength => lengthIsX ? vertexBounds.size.x : vertexBounds.size.z;
        public bool HasShoulder => found;
        public float ShoulderHeight => shoulderY - groundY;
        public float TotalHeight => topY - groundY;
    }

    private bool TrySample(out SizeSample s)
    {
        s = default;
        var frame = MeasureFrame;
        var renderers = GetComponentsInChildren<Renderer>(true);
        var points = new List<Vector3>();
        var excluded = new List<float>();
        foreach (var r in renderers) CollectVertices(r, frame, points, excluded);
        if (points.Count == 0)
        {
            Debug.LogWarning($"{MeasureTag} 정점을 읽지 못해 측정할 수 없습니다");
            return false;
        }

        var vb = new Bounds(points[0], Vector3.zero);
        foreach (var p in points) vb.Encapsulate(p);
        s.points = points;
        s.excluded = excluded;
        s.vertexBounds = vb;
        s.lengthIsX = vb.size.x >= vb.size.z; // 몸 길이 방향 = 수평 바운즈에서 더 긴 축
        s.groundY = vb.min.y;
        s.topY = vb.max.y;

        var anchors = FindShoulderAnchors(out string info);
        s.anchorInfo = info;
        if (anchors.Count == 0) return true;
        s.hasAnchor = true;
        var refP = Vector3.zero;
        foreach (var b in anchors) refP += frame.InverseTransformPoint(b.position);
        refP /= anchors.Count;
        s.shoulderRef = refP;
        s.halfWidth = s.BodyLength * shoulderSliceRatio;

        // 단면 안에서 어깨 본보다 높은 정점만 = 등 윗면 (다리, 배, 가슴 아래쪽 제외).
        // 머리가 앞다리 위로 와 있는 자세가 있으므로 머리/꼬리 본에 붙은 정점도 뺀다
        float center = s.Along(refP);
        var ys = new List<float>();
        var ysAll = new List<float>();
        var ysStrict = new List<float>();
        var sb = new Bounds();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (Mathf.Abs(s.Along(p) - center) > s.halfWidth || p.y <= refP.y) continue;
            ysAll.Add(p.y);
            if (excluded[i] < 0.1f) ysStrict.Add(p.y);
            if (excluded[i] >= shoulderExcludeMaxWeight) continue;
            if (ys.Count == 0) sb = new Bounds(p, Vector3.zero);
            else sb.Encapsulate(p);
            ys.Add(p.y);
        }
        ysAll.Sort();
        ysStrict.Sort();
        s.shoulderYNoExclude = ysAll.Count > 0 ? Percentile(ysAll, shoulderPercentile) : float.NaN;
        s.shoulderYStrict = ysStrict.Count > 0 ? Percentile(ysStrict, shoulderPercentile) : float.NaN;
        s.sliceCount = ys.Count;
        ys.Sort();
        s.sliceShoulderY = ys.Count > 0 ? Percentile(ys, shoulderPercentile) : float.NaN;
        s.sliceMaxY = ys.Count > 0 ? ys[ys.Count - 1] : float.NaN;

        // 기본 방식: 등선이 꺾이는 곳 (목 시작 직전) = 어깨(기갑).
        // 목이 chest 본에 붙어 있어서 가중치만으로는 목과 어깨를 구분할 수 없기 때문
        if (FindWithers(ref s, frame)) return true;

        // 꺾이는 곳을 못 찾으면 엄격한 단면 값 -> 단면 값 순으로 대체
        float fallback = !float.IsNaN(s.shoulderYStrict) ? s.shoulderYStrict : s.sliceShoulderY;
        if (float.IsNaN(fallback)) return true;
        s.found = true;
        s.method = !float.IsNaN(s.shoulderYStrict) ? "앞다리 위 단면 (엄격, 대체)" : "앞다리 위 단면 (대체)";
        s.shoulderY = fallback;
        s.withersPoint = new Vector3(refP.x, fallback, refP.z);
        s.sliceBounds = sb;
        return true;
    }

    // 뒷다리 뿌리 -> 앞다리 뿌리 방향으로 몸통 정점의 윗면 높이를 구간별로 구하고,
    // 기울기가 shoulderNeckSlope를 처음 넘기 직전 구간의 높이를 어깨 높이로 한다
    private bool FindWithers(ref SizeSample s, Transform frame)
    {
        var hips = new List<Transform>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (System.Array.IndexOf(DefaultHipBones, t.name) >= 0) hips.Add(t);
        float hipAlong;
        if (hips.Count > 0)
        {
            float sum = 0f;
            foreach (var h in hips) sum += s.Along(frame.InverseTransformPoint(h.position));
            hipAlong = sum / hips.Count;
        }
        else hipAlong = s.Along(s.vertexBounds.center);

        float frontAlong = s.Along(s.shoulderRef);
        float span = frontAlong - hipAlong;
        float binSize = s.BodyLength / 30f;
        if (Mathf.Abs(span) < binSize * 2f) return false;
        float dir = Mathf.Sign(span);
        int binCount = Mathf.CeilToInt(Mathf.Abs(span) / binSize) + 1;

        var top = new float[binCount];
        for (int i = 0; i < binCount; i++) top[i] = float.NaN;
        for (int i = 0; i < s.points.Count; i++)
        {
            if (s.excluded[i] >= shoulderExcludeMaxWeight) continue;
            var p = s.points[i];
            int bi = Mathf.RoundToInt((s.Along(p) - hipAlong) * dir / binSize);
            if (bi < 0 || bi >= binCount) continue;
            if (float.IsNaN(top[bi]) || p.y > top[bi]) top[bi] = p.y;
        }

        int withers = -1;
        for (int i = 0; i + 1 < binCount; i++)
        {
            if (float.IsNaN(top[i]) || float.IsNaN(top[i + 1])) continue;
            if ((top[i + 1] - top[i]) / binSize > shoulderNeckSlope) { withers = i; break; }
        }
        if (withers < 0) return false;

        float along = hipAlong + dir * withers * binSize;
        s.found = true;
        s.method = $"등선 꺾임 (기울기 {shoulderNeckSlope:0.0} 초과 직전)";
        s.shoulderY = top[withers];
        s.withersPoint = s.AtAlong(along, top[withers], s.shoulderRef);
        s.hipAlong = hipAlong;
        s.frontAlong = frontAlong;
        s.binSize = binSize;

        // 기즈모용: 그 구간 몸통 정점 범위
        bool init = false;
        var b = new Bounds();
        for (int i = 0; i < s.points.Count; i++)
        {
            if (s.excluded[i] >= shoulderExcludeMaxWeight) continue;
            var p = s.points[i];
            if (Mathf.RoundToInt((s.Along(p) - hipAlong) * dir / binSize) != withers) continue;
            if (!init) { b = new Bounds(p, Vector3.zero); init = true; }
            else b.Encapsulate(p);
        }
        s.sliceBounds = b;
        return true;
    }

    // 로드 직후, 크기를 바꾸기 전에 서 있는 자세로 한 번 잰다
    private bool MeasureBase()
    {
        bool snapped = anim != null && walkClip != null;
        if (snapped) SnapToWalkFirstFrame();
        bool ok = TrySample(out var s) && s.HasShoulder;
        if (snapped && idleClip != null) anim.Play(idleClip);
        if (!ok)
        {
            Debug.LogWarning($"{MeasureTag} 어깨 높이 측정 실패 ({s.anchorInfo}). shoulderBoneName을 지정하거나 shoulderSliceRatio를 키우세요");
            return false;
        }

        var frame = MeasureFrame;
        baseScale = transform.localScale;
        baseShoulderHeight = s.ShoulderHeight;
        groundAnchorLocal = transform.InverseTransformPoint(
            frame.TransformPoint(new Vector3(s.vertexBounds.center.x, s.groundY, s.vertexBounds.center.z)));

        // 정점 범위를 이 Transform 로컬로 (회전이 있어도 맞도록 8개 꼭짓점을 모두 변환)
        var vb = s.vertexBounds;
        bool init = false;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? vb.min.x : vb.max.x,
                                     (i & 2) == 0 ? vb.min.y : vb.max.y,
                                     (i & 4) == 0 ? vb.min.z : vb.max.z);
            var local = transform.InverseTransformPoint(frame.TransformPoint(corner));
            if (!init) { baseLocalBounds = new Bounds(local, Vector3.zero); init = true; }
            else baseLocalBounds.Encapsulate(local);
        }

        // 기존 방식(전체 renderer.bounds = targetHeight)이었다면 어깨가 몇 cm였는지 = 자동 기본값.
        // 이 값을 쓰면 스케일이 기존과 정확히 같아진다
        float oldScale = oldFitBoundsHeight > 1e-6f ? targetHeight / oldFitBoundsHeight : 1f;
        autoDefaultShoulderCm = s.ShoulderHeight * oldScale * 100f;
        if (logMeasurement)
            Debug.Log($"{MeasureTag} 기준 측정 (크기 맞추기 전, 스케일 {baseScale.x:0.#####}): 어깨 {s.ShoulderHeight * 100f:0.00} [{s.method}], " +
                      $"전체 {s.TotalHeight * 100f:0.00}, 애니메이션 전 renderer.bounds {oldFitBoundsHeight * 100f:0.00} (모델 단위 x100)\n" +
                      $"  기존 방식(전체 bounds {targetHeight}m, 스케일 {oldScale:0.#####})이었다면 어깨 높이 = {autoDefaultShoulderCm:0.00}cm, " +
                      $"전체 높이 = {s.TotalHeight * oldScale * 100f:0.00}cm\n" +
                      $"  기본값: {DefaultShoulderHeightCm:0.00}cm ({(defaultShoulderHeightCm > 0f ? "인스펙터 지정" : "자동 = 기존 크기 유지")})");
        return true;
    }

    [ContextMenu("크기 측정 로그 출력 (현재 자세)")]
    private void MeasureCurrentPoseFromMenu()
    {
        if (!isLoaded) { Debug.LogWarning($"{MeasureTag} 아직 로드되지 않았습니다"); return; }
        MeasureSize("현재 자세 (메뉴에서 실행)", true);
    }

    private void LogModelInfo()
    {
        var renderers = GetComponentsInChildren<Renderer>(true);
        Debug.Log($"{MeasureTag} Renderer {renderers.Length}개, Dog localScale {transform.localScale.x:0.#####}, " +
                  $"Dog lossyScale {transform.lossyScale.x:0.#####}, 측정 기준 '{MeasureFrame.name}' lossyScale {MeasureFrame.lossyScale}");
        foreach (var r in renderers)
        {
            var mesh = GetSourceMesh(r);
            var mats = System.Array.ConvertAll(r.sharedMaterials, m => m != null ? m.name : "null");
            string extra = r is SkinnedMeshRenderer smr
                ? $", 본 {smr.bones.Length}개, rootBone '{(smr.rootBone != null ? smr.rootBone.name : "없음")}'"
                : "";
            Debug.Log($"{MeasureTag} Renderer '{GetPathSafe(r.transform)}' ({r.GetType().Name}) " +
                      $"정점 {(mesh != null ? mesh.vertexCount : 0)}, 서브메시 {(mesh != null ? mesh.subMeshCount : 0)}, " +
                      $"읽기 가능 {(mesh != null && mesh.isReadable)}, 재질 [{string.Join(", ", mats)}], " +
                      $"bounds 높이 {r.bounds.size.y * 100f:0.0}cm, lossyScale {r.transform.lossyScale.x:0.#####}{extra}");
        }

        // Logcat은 한 줄 길이 제한이 있어서 나눠서 출력
        var all = GetComponentsInChildren<Transform>(true);
        const int chunk = 40;
        for (int i = 0; i < all.Length; i += chunk)
        {
            var names = new List<string>();
            for (int j = i; j < Mathf.Min(i + chunk, all.Length); j++) names.Add(all[j].name);
            Debug.Log($"{MeasureTag} 본/노드 {i + 1}-{i + names.Count} / {all.Length}: {string.Join(", ", names)}");
        }

        LogXrOriginScale();
    }

    private static void LogXrOriginScale()
    {
        var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        if (origin == null)
        {
            Debug.LogWarning($"{MeasureTag} XR Origin을 찾지 못했습니다");
            return;
        }
        string offset = origin.CameraFloorOffsetObject != null
            ? origin.CameraFloorOffsetObject.transform.lossyScale.ToString("0.####") : "없음";
        string cam = origin.Camera != null ? origin.Camera.transform.lossyScale.ToString("0.####") : "없음";
        Debug.Log($"{MeasureTag} XR Origin '{origin.name}' localScale {origin.transform.localScale:0.####}, " +
                  $"lossyScale {origin.transform.lossyScale:0.####}, Camera Offset lossyScale {offset}, Camera lossyScale {cam}" +
                  " (모두 1이어야 AR에서 1 unit = 1m)");
    }

    // 걷기 첫 프레임(서 있는 자세)으로 잠깐 맞춰 재고, idle 재생 상태를 원래대로 돌려놓는다
    private void MeasureStandingPose(string label)
    {
        if (anim == null || walkClip == null)
        {
            MeasureSize(label + " - 걷기 클립 없어 현재 자세", true);
            return;
        }
        SnapToWalkFirstFrame();
        MeasureSize(label, true);
        if (idleClip != null) anim.Play(idleClip);
    }

    // 현재 자세를 재서 로그와 기즈모로 보여준다 (크기에는 영향 없음)
    private void MeasureSize(string label, bool logDetails)
    {
        if (!TrySample(out var s)) return;
        var frame = MeasureFrame;
        var vb = s.vertexBounds;
        float groundY = s.groundY;
        float Cm(float y) => (y - groundY) * 100f;

        var rb = GetBounds(GetComponentsInChildren<Renderer>(true));
        float boundsHeight = frame.InverseTransformVector(new Vector3(0f, rb.size.y, 0f)).magnitude;
        float boundsMinY = frame.InverseTransformPoint(rb.min).y;

        var log = new System.Text.StringBuilder();
        log.AppendLine($"{MeasureTag} [{label}]");
        log.AppendLine($"  정점 {s.points.Count}개, 몸 길이 축 {(s.lengthIsX ? "X" : "Z")}, 몸 길이 {s.BodyLength * 100f:0.0}cm, 폭 {(s.lengthIsX ? vb.size.z : vb.size.x) * 100f:0.0}cm");
        log.AppendLine($"  전체 높이: 정점 기준 {s.TotalHeight * 100f:0.0}cm, renderer.bounds 기준 {boundsHeight * 100f:0.0}cm");
        log.AppendLine($"  바닥: 가장 낮은 정점이 '{frame.name}' 원점보다 {groundY * 100f:+0.00;-0.00}cm (0이면 발이 바닥에 닿음), " +
                       $"renderer.bounds 바닥은 {boundsMinY * 100f:+0.00;-0.00}cm");
        log.AppendLine($"  어깨 기준: {s.anchorInfo}");
        if (s.hasAnchor)
            log.AppendLine($"  어깨 단면: 위치 {(s.Along(s.shoulderRef) - s.Along(vb.center)) * 100f:+0.0;-0.0}cm (몸 중앙 기준), 반폭 {s.halfWidth * 100f:0.0}cm, " +
                           $"어깨 본 높이 {Cm(s.shoulderRef.y):0.0}cm 위 정점 {s.sliceCount}개");
        if (s.HasShoulder)
        {
            string pos = s.binSize > 0f
                ? $", 위치 {(s.Along(s.withersPoint) - s.Along(vb.center)) * 100f:+0.0;-0.0}cm (뒷다리 {(s.hipAlong - s.Along(vb.center)) * 100f:+0.0;-0.0}, 앞다리 {(s.frontAlong - s.Along(vb.center)) * 100f:+0.0;-0.0})"
                : "";
            log.AppendLine($"  => 어깨 높이 {s.ShoulderHeight * 100f:0.0}cm [{s.method}{pos}] / 전체 높이 {s.TotalHeight * 100f:0.0}cm " +
                           $"= {s.ShoulderHeight / Mathf.Max(s.TotalHeight, 1e-6f):0.000}  (적용 값 {appliedShoulderCm:0.0}cm)");
        }
        else
            log.AppendLine("  어깨 높이를 찾지 못했습니다. shoulderBoneName / shoulderNeckSlope를 확인하세요");
        if (s.hasAnchor)
            log.AppendLine($"  비교 (앞다리 위 단면): 머리/꼬리 가중치 {shoulderExcludeMaxWeight:0.00} 미만 {Cm(s.sliceShoulderY):0.0}cm, " +
                           $"제외 없이 {Cm(s.shoulderYNoExclude):0.0}cm, 엄격하게(0.1 미만) {Cm(s.shoulderYStrict):0.0}cm");
        Debug.Log(log.ToString());

        if (logDetails)
        {
            LogCandidateBones(frame, groundY, s.Along, s.Along(vb.center));
            LogTopProfile(s.points, s.excluded, frame, groundY, s.Along, s.lengthIsX ? vb.min.x : vb.min.z, s.BodyLength, s.Along(vb.center));
        }

        // 기즈모용으로 이 Transform 로컬 좌표에 저장 (강아지가 움직여도 따라감)
        if (s.HasShoulder)
        {
            Vector3 ToLocal(Vector3 p) => transform.InverseTransformPoint(frame.TransformPoint(p));
            gizmoGround = ToLocal(new Vector3(s.withersPoint.x, groundY, s.withersPoint.z));
            gizmoShoulder = ToLocal(s.withersPoint);
            gizmoTop = ToLocal(new Vector3(vb.center.x, s.topY, vb.center.z));
            gizmoLengthIsX = s.lengthIsX;
            var size = transform.InverseTransformVector(frame.TransformVector(s.sliceBounds.size));
            gizmoSlice = new Bounds(ToLocal(s.sliceBounds.center), new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)));
            hasMeasurement = true;
            UpdateShoulderMarker();
        }
    }

    // 기기에서도 보이도록 어깨 위치를 초록 선으로 그린다 (바닥 -> 어깨, 어깨에서 짧은 가로선)
    private void UpdateShoulderMarker()
    {
        if (!showShoulderMarker)
        {
            if (shoulderMarker != null) shoulderMarker.enabled = false;
            return;
        }
        if (shoulderMarker == null)
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) { Debug.LogWarning($"{MeasureTag} 어깨 표시선 셰이더(Sprites/Default)가 없습니다"); return; }
            var go = new GameObject("ShoulderMarker (debug)");
            go.transform.SetParent(transform, false);
            shoulderMarker = go.AddComponent<LineRenderer>();
            shoulderMarker.useWorldSpace = false;
            shoulderMarker.material = new Material(shader);
            shoulderMarker.startColor = shoulderMarker.endColor = Color.green;
            shoulderMarker.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            shoulderMarker.receiveShadows = false;
            shoulderMarker.widthMultiplier = 0.004f;
        }
        shoulderMarker.enabled = true;
        // 가로선은 몸 옆쪽(좌우)으로 8cm씩 뻗게 해서 몸 밖에서도 보이게 함
        var frame = MeasureFrame;
        var sideLocal = transform.InverseTransformVector((gizmoLengthIsX ? frame.forward : frame.right) * 0.08f);
        shoulderMarker.positionCount = 4;
        shoulderMarker.SetPositions(new[] { gizmoGround, gizmoShoulder, gizmoShoulder + sideLocal, gizmoShoulder - sideLocal });
    }

    private void LogCandidateBones(Transform frame, float groundY, System.Func<Vector3, float> along, float centerAlong)
    {
        var lines = new List<string>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            var n = t.name.ToLowerInvariant();
            bool match = false;
            foreach (var k in CandidateBoneKeywords) if (n.Contains(k)) { match = true; break; }
            if (!match) continue;
            var p = frame.InverseTransformPoint(t.position);
            lines.Add($"{t.name}: 높이 {(p.y - groundY) * 100f:0.0}cm, 앞뒤 {(along(p) - centerAlong) * 100f:+0.0;-0.0}cm");
        }
        if (lines.Count == 0)
        {
            Debug.Log($"{MeasureTag} 어깨/다리 후보 본 없음");
            return;
        }
        const int chunk = 25;
        for (int i = 0; i < lines.Count; i += chunk)
            Debug.Log($"{MeasureTag} 후보 본 {i + 1}-{Mathf.Min(i + chunk, lines.Count)} / {lines.Count}\n  " +
                      string.Join("\n  ", lines.GetRange(i, Mathf.Min(chunk, lines.Count - i))));
    }

    // 몸 길이 방향으로 2cm 구간마다 가장 높은 점(윗면 실루엣)을 출력. 등선 / 목 / 머리를 구분해 어깨 위치를 정하기 위함
    private void LogTopProfile(List<Vector3> points, List<float> excluded, Transform frame, float groundY,
        System.Func<Vector3, float> along, float minAlong, float bodyLength, float centerAlong)
    {
        const float binSize = 0.02f;
        int binCount = Mathf.Max(1, Mathf.CeilToInt(bodyLength / binSize));
        var bins = new List<float>[binCount];
        var bodyMax = new float[binCount]; // 머리/꼬리 제외한 몸통 정점의 최대 높이
        for (int i = 0; i < binCount; i++) { bins[i] = new List<float>(); bodyMax[i] = float.NaN; }
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            int bi = Mathf.Clamp((int)((along(p) - minAlong) / binSize), 0, binCount - 1);
            bins[bi].Add(p.y);
            if (excluded[i] < shoulderExcludeMaxWeight && (float.IsNaN(bodyMax[bi]) || p.y > bodyMax[bi])) bodyMax[bi] = p.y;
        }

        // 구간 안에 들어오는 본 표시 (앞다리 뿌리 = 어깨 위치 후보)
        var markers = new Dictionary<int, List<string>>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "chest" && t.name != "head" && t.name != "frontleg" && t.name != "R_frontleg" &&
                t.name != "backleg" && t.name != "R_backleg") continue;
            int bi = Mathf.Clamp((int)((along(frame.InverseTransformPoint(t.position)) - minAlong) / binSize), 0, binCount - 1);
            if (!markers.TryGetValue(bi, out var list)) markers[bi] = list = new List<string>();
            list.Add(t.name);
        }

        var lines = new List<string>();
        for (int i = 0; i < binCount; i++)
        {
            float from = (minAlong + i * binSize - centerAlong) * 100f;
            string mark = markers.TryGetValue(i, out var names) ? $"  <- {string.Join(", ", names)}" : "";
            if (bins[i].Count == 0) { lines.Add($"{from:+0;-0}~{from + binSize * 100f:+0;-0}cm: (정점 없음){mark}"); continue; }
            bins[i].Sort();
            string body = float.IsNaN(bodyMax[i]) ? "-" : $"{(bodyMax[i] - groundY) * 100f:0.0}";
            lines.Add($"{from:+0;-0}~{from + binSize * 100f:+0;-0}cm: 최대 {(bins[i][bins[i].Count - 1] - groundY) * 100f:0.0}, " +
                      $"몸통만 최대 {body} (정점 {bins[i].Count}){mark}");
        }
        const int chunk = 25;
        for (int i = 0; i < lines.Count; i += chunk)
            Debug.Log($"{MeasureTag} 윗면 실루엣 (앞뒤 위치 기준, 높이 cm) {i + 1}-{Mathf.Min(i + chunk, lines.Count)} / {lines.Count}\n  " +
                      string.Join("\n  ", lines.GetRange(i, Mathf.Min(chunk, lines.Count - i))));
    }

    // 어깨 위치 기준 본: 지정한 이름 -> 앞다리 뿌리(frontleg, R_frontleg) -> 이름 키워드 순으로 찾는다
    private List<Transform> FindShoulderAnchors(out string info)
    {
        var all = GetComponentsInChildren<Transform>(true);
        var result = new List<Transform>();
        bool custom = !string.IsNullOrWhiteSpace(shoulderBoneName);
        var names = custom
            ? System.Array.ConvertAll(shoulderBoneName.Split(','), n => n.Trim())
            : DefaultShoulderBones;
        foreach (var t in all)
            if (System.Array.IndexOf(names, t.name) >= 0) result.Add(t);
        if (result.Count > 0)
        {
            info = $"본 [{string.Join(", ", result.ConvertAll(b => b.name))}]{(custom ? " (지정)" : "")}";
            return result;
        }
        if (custom) Debug.LogWarning($"{MeasureTag} 지정한 어깨 본 없음: {shoulderBoneName}");

        // 좌우 한 쌍일 수 있으므로 같은 키워드에 걸린 본을 모두 모아 평균 위치를 씀
        foreach (var k in FallbackShoulderKeywords)
        {
            foreach (var t in all)
                if (t.name.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0) result.Add(t);
            if (result.Count > 0)
            {
                info = $"본 [{string.Join(", ", result.ConvertAll(b => b.name))}] (대체 키워드 '{k}', 부정확할 수 있음)";
                return result;
            }
        }
        info = "어깨 기준 본 없음";
        return result;
    }

    private static Mesh GetSourceMesh(Renderer r)
    {
        if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
        return r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
    }

    // 현재 자세의 정점을 frame 로컬 좌표로 모은다
    // bone 자신이나 조상(이 Transform 아래) 이름에 제외 키워드가 있으면 true
    private bool IsExcludedBone(Transform bone, string[] keywords)
    {
        for (var t = bone; t != null && t != transform; t = t.parent)
            foreach (var k in keywords)
                if (k.Length > 0 && t.name.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private void CollectVertices(Renderer r, Transform frame, List<Vector3> output, List<float> excludedOut)
    {
        var mesh = GetSourceMesh(r);
        if (mesh == null) return;
        if (!mesh.isReadable)
        {
            Debug.LogWarning($"{MeasureTag} 메시를 읽을 수 없어 건너뜀: '{r.name}'");
            return;
        }

        var verts = mesh.vertices;
        var toFrame = frame.worldToLocalMatrix;
        var smr = r as SkinnedMeshRenderer;
        var bones = smr != null ? smr.bones : null;
        var bindposes = smr != null ? mesh.bindposes : null;
        var weights = smr != null ? mesh.boneWeights : null;

        if (smr != null && bones.Length > 0 && bindposes.Length == bones.Length && weights.Length == verts.Length)
        {
            // GPU 스키닝과 같은 계산을 CPU에서 직접 한다: 월드 = sum(가중치 x 본 localToWorld x 바인드포즈) x 정점.
            // BakeMesh의 스케일 처리 방식에 의존하지 않기 위함
            for (int i = 0; i < mesh.blendShapeCount; i++)
                if (smr.GetBlendShapeWeight(i) != 0f)
                {
                    Debug.LogWarning($"{MeasureTag} '{r.name}' 블렌드셰이프는 측정에 반영하지 않습니다");
                    break;
                }
            var m = new Matrix4x4[bones.Length];
            var ex = new float[bones.Length];
            var keywords = System.Array.ConvertAll(shoulderExcludeBones.Split(','), k => k.Trim());
            for (int i = 0; i < bones.Length; i++)
            {
                m[i] = toFrame * (bones[i] != null ? bones[i].localToWorldMatrix : r.transform.localToWorldMatrix) * bindposes[i];
                ex[i] = bones[i] != null && IsExcludedBone(bones[i], keywords) ? 1f : 0f;
            }
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                var w = weights[i];
                output.Add(m[w.boneIndex0].MultiplyPoint3x4(v) * w.weight0 +
                           m[w.boneIndex1].MultiplyPoint3x4(v) * w.weight1 +
                           m[w.boneIndex2].MultiplyPoint3x4(v) * w.weight2 +
                           m[w.boneIndex3].MultiplyPoint3x4(v) * w.weight3);
                excludedOut.Add(ex[w.boneIndex0] * w.weight0 + ex[w.boneIndex1] * w.weight1 +
                                ex[w.boneIndex2] * w.weight2 + ex[w.boneIndex3] * w.weight3);
            }
            return;
        }

        if (smr != null)
            Debug.LogWarning($"{MeasureTag} '{r.name}' 스킨 정보가 없어 정지 메시로 처리합니다 " +
                             $"(본 {bones.Length}, 바인드포즈 {bindposes.Length}, 가중치 {weights.Length}/{verts.Length})");
        var toFrameMesh = toFrame * r.transform.localToWorldMatrix;
        foreach (var v in verts)
        {
            output.Add(toFrameMesh.MultiplyPoint3x4(v));
            excludedOut.Add(0f);
        }
    }

    private static float Percentile(List<float> sorted, float p)
    {
        int i = Mathf.Clamp(Mathf.RoundToInt(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[i];
    }

    private string GetPathSafe(Transform t) => t == transform ? t.name : GetPath(transform, t);

    private void OnDrawGizmosSelected()
    {
        if (!hasMeasurement) return;
        var ground = transform.TransformPoint(gizmoGround);
        var shoulder = transform.TransformPoint(gizmoShoulder);
        var top = transform.TransformPoint(gizmoTop);

        Gizmos.color = Color.green;   // 바닥 -> 어깨
        Gizmos.DrawLine(ground, shoulder);
        Gizmos.DrawWireSphere(shoulder, 0.008f);
        Gizmos.color = Color.yellow;  // 전체 높이
        Gizmos.DrawLine(new Vector3(top.x, ground.y, top.z), top);
        Gizmos.DrawWireSphere(top, 0.008f);

        Gizmos.color = Color.cyan;    // 어깨 단면 범위
        var old = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(gizmoSlice.center, gizmoSlice.size);
        Gizmos.matrix = old;
    }
}
