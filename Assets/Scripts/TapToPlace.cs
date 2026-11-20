using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class TapToPlace : MonoBehaviour
{
    [SerializeField] ARRaycastManager raycastManager;
    [SerializeField] Transform dogRoot;
    [SerializeField] GlbRuntimeLoader dog;
    [SerializeField] float yawOffset;
    [SerializeField] ARPlaneManager planeManager;

    [Header("Move")]
    [SerializeField] float moveSpeed = 0.3f;       // m/s
    [SerializeField] float turnSpeed = 360f;       // deg/s
    [SerializeField] float arriveDistance = 0.02f; // m
    [Tooltip("목표까지 이 거리 안에 들어오면 감속 시작")]
    [SerializeField] float slowDownDistance = 0.15f; // m
    [Tooltip("감속 구간 끝에서의 최소 속도 비율")]
    [SerializeField, Range(0.1f, 1f)] float minSpeedFactor = 0.3f;

    [Header("Plane Look")]
    [SerializeField] Color planeFillColor = new(0.72f, 0.88f, 0.93f, 0.18f); // 연한 하늘색, 반투명
    [SerializeField] Color planeLineColor = new(1f, 1f, 1f, 0.35f);          // 흐린 흰색 테두리

    static readonly List<ARRaycastHit> hits = new();
    bool placed;
    bool moving;
    Vector3 target;
    Material planeMaterial;

    private void Awake()
    {
        dog.Loaded += OnDogLoaded;
    }
    private void OnDestroy()
    {
        dog.Loaded -= OnDogLoaded;
        if (planeMaterial != null) Destroy(planeMaterial);
    }

    private void OnEnable()
    {
        planeManager.trackablesChanged.AddListener(OnPlanesChanged);
        foreach (var plane in planeManager.trackables) StylePlane(plane);
    }
    private void OnDisable()
    {
        planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
    }

    private void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
    {
        foreach (var plane in args.added) StylePlane(plane);
    }

    private void StylePlane(ARPlane plane)
    {
        if (plane.TryGetComponent<MeshRenderer>(out var meshRenderer))
        {
            // 평면마다 머티리얼을 복제하지 않도록 하나를 만들어 공유
            if (planeMaterial == null)
            {
                planeMaterial = new Material(meshRenderer.sharedMaterial);
                var fill = planeFillColor;
                // DebugPlane 머티리얼은 Premultiply 블렌딩이라 RGB에 알파를 미리 곱해야 의도한 투명도가 나옴
                if (planeMaterial.HasProperty("_SrcBlend") &&
                    Mathf.Approximately(planeMaterial.GetFloat("_SrcBlend"), (float)UnityEngine.Rendering.BlendMode.One))
                {
                    fill.r *= fill.a; fill.g *= fill.a; fill.b *= fill.a;
                }
                if (planeMaterial.HasProperty("_BaseColor")) planeMaterial.SetColor("_BaseColor", fill);
                if (planeMaterial.HasProperty("_Color")) planeMaterial.SetColor("_Color", fill);
            }
            meshRenderer.sharedMaterial = planeMaterial;
        }
        if (plane.TryGetComponent<LineRenderer>(out var line))
        {
            line.startColor = planeLineColor;
            line.endColor = planeLineColor;
        }
    }

    private void OnDogLoaded()
    {
        if (!placed) dogRoot.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (placed) HidePlanes();
        if (moving) MoveTowardsTarget();
        if (!dog.IsLoaded) return;
        var pointer = Pointer.current;
        if (pointer == null || !pointer.press.wasPressedThisFrame) return;
        if (!raycastManager.Raycast(pointer.position.ReadValue(), hits, TrackableType.PlaneWithinPolygon)) return;

        var pose = hits[0].pose;
        if (placed)
        {
            SetTarget(pose.position);
            return;
        }

        var toCamera = Camera.main.transform.position - pose.position;
        toCamera.y = 0f;
        var rotation = Quaternion.LookRotation(toCamera) * Quaternion.Euler(0f, yawOffset, 0f);

        dogRoot.SetPositionAndRotation(pose.position, rotation);
        dogRoot.gameObject.SetActive(true);
        placed = true;
    }

    private void SetTarget(Vector3 point)
    {
        // 처음 놓은 바닥 높이를 유지 (다른 평면을 눌러도 공중으로 걸어가지 않음)
        point.y = dogRoot.position.y;
        if ((point - dogRoot.position).sqrMagnitude <= arriveDistance * arriveDistance) return;

        target = point;
        if (!moving)
        {
            moving = true;
            dog.PlayWalk();
        }
    }

    private void MoveTowardsTarget()
    {
        var position = dogRoot.position;
        var toTarget = target - position;
        toTarget.y = 0f;

        if (toTarget.sqrMagnitude <= arriveDistance * arriveDistance)
        {
            dogRoot.position = target;
            moving = false;
            dog.StopWalk();
            return;
        }

        // 도착 직전에 서서히 감속하고, 걷기 애니메이션 속도도 같이 낮춤
        float speedFactor = Mathf.Lerp(minSpeedFactor, 1f,
            Mathf.Clamp01(toTarget.magnitude / Mathf.Max(slowDownDistance, 0.0001f)));
        dog.SetWalkSpeed(speedFactor);

        var lookRotation = Quaternion.LookRotation(toTarget) * Quaternion.Euler(0f, yawOffset, 0f);
        dogRoot.SetPositionAndRotation(
            Vector3.MoveTowards(position, target, moveSpeed * speedFactor * Time.deltaTime),
            Quaternion.RotateTowards(dogRoot.rotation, lookRotation, turnSpeed * Time.deltaTime));
    }

    private void HidePlanes()
    {
        foreach(var plane in planeManager.trackables)
            if (plane.gameObject.activeSelf) plane.gameObject.SetActive(false);
    }
}
