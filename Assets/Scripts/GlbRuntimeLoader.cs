using GLTFast;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

public class GlbRuntimeLoader : MonoBehaviour
{
    [SerializeField]
    private string fileName = "Fox.glb";

    private Animation anim;
    async void Start()
    {
        string path = Path.Combine(Application.persistentDataPath, fileName);
        Debug.Log($"[GlbLoader] 경로 : {path}");

        if (!File.Exists(path))
        {
            Debug.LogError($"[GlbLoader] 파일이 없습니다: {path}");
            return;
        }

        var settings = new ImportSettings
        {
            AnimationMethod = AnimationMethod.Legacy
        };

        var gltf = new GltfImport();

        if (!await gltf.Load(path))
        {
            Debug.LogError("[GlbLoader] GLB 로드 실패");
            return;
        }

        if (!await gltf.InstantiateMainSceneAsync(transform))
        {
            Debug.LogError("[GlbLoader] 생성 실패");
            return;
        }

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
}
