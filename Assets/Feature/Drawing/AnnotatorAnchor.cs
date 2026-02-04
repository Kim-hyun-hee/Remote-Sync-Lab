using UnityEngine;

/// <summary>
/// Scene 내에서 "현재 사용할 IOverlayAnnotator"를 고정적으로 제공하는 앵커.
/// 
/// AnnotationHub는 Spawned 시점에:
/// - AnnotatorAnchor.Instance가 있으면 그 Annotator를 우선 사용.
/// - 없으면 Scene 전체에서 IOverlayAnnotator를 탐색.
/// </summary>
public sealed class AnnotatorAnchor : MonoBehaviour
{
    /// <summary>싱글턴 인스턴스</summary>
    public static AnnotatorAnchor Instance { get; private set; }

    /// <summary>
    /// 인스펙터에 할당할 annotator Behaviour.
    /// - Texture2DOverlayAnnotator 또는 UIToolkitOverlayAnnotator 등.
    /// </summary>
    [SerializeField] private MonoBehaviour annotatorBehaviour;

    /// <summary>
    /// 실제 인터페이스로 노출.
    /// </summary>
    public IOverlayAnnotator Annotator => annotatorBehaviour as IOverlayAnnotator;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
