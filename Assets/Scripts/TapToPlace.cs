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

    static readonly List<ARRaycastHit> hits = new();
    bool placed;

    private void Awake()
    {
        dog.Loaded += OnDogLoaded;
    }
    private void OnDestroy()
    {
        dog.Loaded -= OnDogLoaded;
    }

    private void OnDogLoaded()
    {
        if (!placed) dogRoot.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (placed) HidePlanes();
        if (!dog.IsLoaded) return;
        var pointer = Pointer.current;
        if (pointer == null || !pointer.press.wasPressedThisFrame) return;
        if (!raycastManager.Raycast(pointer.position.ReadValue(), hits, TrackableType.PlaneWithinPolygon)) return;

        var pose = hits[0].pose;
        var toCamera = Camera.main.transform.position - pose.position;
        toCamera.y = 0f;
        var rotation = Quaternion.LookRotation(toCamera) * Quaternion.Euler(0f, yawOffset, 0f);

        dogRoot.SetPositionAndRotation(pose.position, rotation);
        dogRoot.gameObject.SetActive(true);
        placed = true;
    }

    private void HidePlanes()
    {
        foreach(var plane in planeManager.trackables)
            if (plane.gameObject.activeSelf) plane.gameObject.SetActive(false);
    }
}