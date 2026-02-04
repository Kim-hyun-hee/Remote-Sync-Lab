using System;
using UnityEngine;

/// <summary>
/// 하나의 스트로크(선)를 유일하게 식별하기 위한 키.
/// 
/// - AuthorId: "누가 그렸는지"를 구분하는 값.
///   - 네트워크에서는 PlayerRef.GetHashCode() 값을 사용 (AnnotationHub에서 생성)
///   - 로컬 입력(네트워크 없이 그림)에서는 -1 같은 고정값을 사용해도 됨
/// 
/// - StrokeId: 해당 작성자가 로컬에서 증가시키는 스트로크 번호.
///   - 예: 1번 선, 2번 선, 3번 선...
/// 
/// ※ 중요한 점
///   (AuthorId, StrokeId) 쌍이 같을 때만 같은 선으로 취급.
///   작성자가 다르면 StrokeId가 같아도 다른 선이다.
/// </summary>
public readonly struct OverlayStrokeKey : System.IEquatable<OverlayStrokeKey>
{
    /// <summary>스트로크 작성자 식별자</summary>
    public readonly int AuthorId;

    /// <summary>작성자가 부여한 스트로크 번호</summary>
    public readonly int StrokeId;

    /// <summary>
    /// 스트로크 키 생성자.
    /// </summary>
    public OverlayStrokeKey(int authorId, int strokeId)
    {
        AuthorId = authorId;
        StrokeId = strokeId;
    }

    /// <summary>
    /// Dictionary 키로 사용하기 위한 동등 비교.
    /// </summary>
    public bool Equals(OverlayStrokeKey other)
        => AuthorId == other.AuthorId && StrokeId == other.StrokeId;

    public override bool Equals(object obj)
        => obj is OverlayStrokeKey other && Equals(other);

    /// <summary>
    /// Dictionary 키로 사용하기 위한 해시코드.
    /// </summary>
    public override int GetHashCode()
    {
        return HashCode.Combine(AuthorId, StrokeId);
    }

    public static bool operator ==(OverlayStrokeKey a, OverlayStrokeKey b) => a.Equals(b);
    public static bool operator !=(OverlayStrokeKey a, OverlayStrokeKey b) => !a.Equals(b);

    public override string ToString()
        => $"OverlayStrokeKey(author={AuthorId}, stroke={StrokeId})";
}

/// <summary>
/// 오버레이(화면 위) 드로잉을 수행하는 공통 인터페이스.
/// 
/// 중요한 변경점:
/// - Begin/Add/End가 "현재 스트로크 1개"가 아니라,
///   OverlayStrokeKey를 통해 "여러 스트로크를 동시에" 처리할 수 있게 설계.
/// </summary>
public interface IOverlayAnnotator
{
    /// <summary>
    /// 렌더링 대상(UI/텍스처 등)이 준비되어 입력/그리기가 가능한 상태인지 여부.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// 오버레이 표시 활성/비활성.
    /// - 비활성 시 입력을 받더라도 보이지 않는 처리 등을 할 수 있음.
    /// </summary>
    void SetActive(bool active);

    /// <summary>
    /// 화면 좌표(Screen) -> 오버레이 기준 정규화 좌표(0~1) 변환.
    /// - 성공하면 normalized에 값이 채워지고 true 반환.
    /// - 실패하면 false 반환 (오버레이 밖을 클릭했거나 준비 미완료 등)
    /// </summary>
    bool TryScreenToNormalized(Vector2 screenPos, out Vector2 normalized);

    /// <summary>
    /// 현재 오버레이 렌더링 영역의 픽셀 크기.
    /// - min distance 같은 정규화 거리 계산에 사용.
    /// </summary>
    Vector2 GetRenderSizePx();

    // ===== 드로잉(스트로크) =====

    /// <summary>
    /// 특정 스트로크(key)에 대한 그리기 시작.
    /// - key: (작성자, 스트로크ID)로 유일한 선을 식별
    /// - color/widthPx: 선 스타일
    /// </summary>
    void BeginStroke(OverlayStrokeKey key, Color color, float widthPx);

    /// <summary>
    /// 특정 스트로크(key)에 점(정규화 좌표)을 추가.
    /// </summary>
    void AddStrokePoint(OverlayStrokeKey key, Vector2 normalized);

    /// <summary>
    /// 특정 스트로크(key) 종료.
    /// </summary>
    void EndStroke(OverlayStrokeKey key);

    // ===== 텍스트 =====

    /// <summary>
    /// 지정 위치(정규화)에 텍스트 추가.
    /// </summary>
    void AddText(Vector2 normalized, string text);

    // ===== 전체 초기화 =====

    /// <summary>
    /// 모든 스트로크/텍스트 제거.
    /// </summary>
    void Clear();
}
