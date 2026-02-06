using System;
using System.Collections.Generic;
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
/// </summary>
public interface IOverlayAnnotator
{
    /// <summary>
    /// 렌더링 대상(UI/텍스처 등)이 준비되어 입력/그리기가 가능한 상태인지 여부.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// 화면 좌표(Screen) -> 오버레이 기준 정규화 좌표(0~1) 변환.
    /// - 성공하면 normalized에 값이 채워지고 true 반환.
    /// - 실패하면 false 반환 (오버레이 밖을 클릭했거나 준비 미완료 등)
    /// 
    /// 왜 정규화 좌표를 쓰나?
    /// - 서로 다른 해상도/윈도우 크기에서도 동일한 위치를 공유하기 위해.
    /// - 네트워크로 보낼 때 float 그대로 보내면 payload가 커지므로,
    ///   정규화 좌표를 ushort 패킹으로 줄이기에도 유리.
    /// </summary>
    bool TryScreenToNormalized(Vector2 screenPos, out Vector2 normalized);

    /// <summary>
    /// 벌크 모드 시작.
    /// 
    /// 왜 필요하나?
    /// - Late Join 스냅샷 재생 시 포인트 수천~수만개가 한 번에 들어올 수 있음.
    /// - 이때 AddPoints마다 리페인트/업데이트가 발생하면 프레임이 찢어진다.
    /// - BeginBulk~EndBulk로 묶어서 "마지막에 한 번만" 리페인트 하게 만들면 비용이 크게 줄어듦.
    /// </summary>
    void BeginBulk();

    /// <summary>
    /// 벌크 모드 종료(필요하면 여기서 최종 리페인트).
    /// </summary>
    void EndBulk();

    void BeginStroke(OverlayStrokeKey key, Color32 color, float widthPx);
    void AddPoints(OverlayStrokeKey key, IReadOnlyList<Vector2> normPoints);
    void EndStroke(OverlayStrokeKey key);

    void AddLabel(int labelId, Vector2 normPos, string text);
    void ClearAll();
}
