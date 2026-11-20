using GLTFast;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class GlbRuntimeLoader : MonoBehaviour
{
    [SerializeField] private string fileName = "SampleDog.glb";
    [SerializeField] private float targetHeight = 0.5f;
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

        FitToHeight(targetHeight);
        // Loaded 구독자가 오브젝트를 비활성화할 수 있으므로 애니메이션 준비를 먼저 끝낸다
        SetupAnimation();
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
        if (!anim.IsPlaying(walkClip)) anim.CrossFade(walkClip, crossFadeTime);
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
    }

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
    private Bounds GetBounds(Renderer[] renderers)
    {
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }
}
