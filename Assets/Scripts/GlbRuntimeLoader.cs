using GLTFast;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

public class GlbRuntimeLoader : MonoBehaviour
{
    [SerializeField] private string fileName = "Fox.glb";
    [SerializeField] private float targetHeight = 0.5f;

    private Animation anim;
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


        anim = GetComponentInChildren<Animation>();
        if (anim == null)
        {
            Debug.LogError("[GlbLoader] Animation 컴포넌트가 없습니다");
            return;
        }
        foreach (AnimationState state in anim)
        {
            Debug.Log($"[GlbLoader] 클립 : {state.name}");
        }
    }
    private void Update()
    {
        if (anim == null || Keyboard.current == null) return;

        if (Keyboard.current.digit1Key.wasPressedThisFrame) PlayClip("Survey");
        if (Keyboard.current.digit2Key.wasPressedThisFrame) PlayClip("Walk");
        if (Keyboard.current.digit3Key.wasPressedThisFrame) PlayClip("Run");
    }

    private void PlayClip(string clipName)
    {
        if (anim.GetClip(clipName) == null)
        {
            Debug.LogWarning($"[GlbLoader] 클립 없음: {clipName}");
            return;
        }

        anim.CrossFade(clipName, 0.2f);
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
    private Bounds GetBounds(Renderer[] renderers)
    {
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }
}
