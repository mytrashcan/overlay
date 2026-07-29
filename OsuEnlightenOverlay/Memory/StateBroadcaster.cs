using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using OsuEnlightenOverlay.Gameplay.Difficulty;
using OsuEnlightenOverlay.Rendering;

namespace OsuEnlightenOverlay.Memory
{
    /// <summary>
    /// 공유 메모리(Memory-Mapped File)에 OsuMemoryReader의 live 상태를 매 프레임 write.
    /// Reconstructor(OpenTabletDriver 플러그인, .NET 8)가 이 MMF를 읽어 aim assist 데이터로 사용.
    ///
    /// 스레드 모델:
    ///   writer = OverlayForm의 UI 스레드 단일 (OnSyncTick에서만 호출)
    ///   reader = Reconstructor의 OTD 스레드 (외부 프로세스)
    /// 단일 writer이므로 writer 쪽 락 불필요. torn read 방지는 Sequence 필드로 처리 —
    /// reader가 read 전/후의 Sequence가 같으면 온전한 스냅샷으로 간주.
    ///
    /// MMF 이름은 "Global\ReconstructorState" — OsuEnlightenOverlay2가 관리자 권한으로 실행되므로
    /// Global namespace에 생성 가능. Reconstructor는 OpenExisting로 attach.
    /// </summary>
    public sealed class StateBroadcaster : IDisposable
    {
        const string MapName = "Global\\ReconstructorState";

        MemoryMappedFile mmf;
        MemoryMappedViewAccessor accessor;
        byte[] stringAreaBuffer; // 문자열 write용 scratch 버퍼 (매 프레임 new 방지)
        int sequence;             // 단조 증가 시퀀스 — torn read 검증용

        // 문자열 영역 전체를 한 번에 write하기 위한 scratch (StringAreaSize = 2048)
        static readonly int StringAreaSize = SharedStateLayout.StringAreaSize;

        public bool IsActive { get { return mmf != null; } }

        /// <summary>
        /// MMF를 생성(또는 기존 것이 있으면 재사용). 실패해도 치명적이지 않음 —
        /// 오버레이 자체 기능엔 영향 안 주고, Reconstructor가 attach 못 할 뿐.
        /// </summary>
        public StateBroadcaster()
        {
            try
            {
                mmf = MemoryMappedFile.CreateOrOpen(MapName, SharedStateLayout.TotalSize,
                                                    MemoryMappedFileAccess.ReadWrite);
                accessor = mmf.CreateViewAccessor(0, SharedStateLayout.TotalSize,
                                                  MemoryMappedFileAccess.ReadWrite);
                stringAreaBuffer = new byte[StringAreaSize];
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Broadcaster] MMF 생성 실패 — Reconstructor 연결 안 됨: " + ex.Message);
                mmf = null;
                accessor = null;
            }
        }

        /// <summary>
        /// 한 프레임 분량의 스냅샷을 MMF에 write.
        /// UI 스레드에서만 호출됨 (OnSyncTick). reader는 read 전/후 Sequence가 같은지 검증.
        /// </summary>
        /// <param name="difficulty">Difficulty Changer override가 반영된 최종 난이도 값. null이면
        /// 아직 맵이 로드되지 않은 것 — DifficultyReady=0으로 쓰고, Reconstructor는 raw AR/CS 폴백.</param>
        /// <param name="gameField">오버레이의 GameField — 좌표 변환 파라미터(Offset/Ratio). null이면
        /// Reconstructor는 자체 폴백 공식으로 좌표 변환 (오차 발생 가능).</param>
        /// <param name="osuWindowX">osu! 창 왼쪽 위 X (화면 좌표). OTD 좌표계 변환용.</param>
        /// <param name="osuWindowY">osu! 창 왼쪽 위 Y (화면 좌표).</param>
        internal void WriteSnapshot(OsuMemoryReader reader, DifficultyValues difficulty, GameField gameField,
                                     int osuWindowX, int osuWindowY)
        {
            if (accessor == null) return;

            int seq = System.Threading.Interlocked.Increment(ref sequence);

            var state = new SharedState
            {
                Sequence   = seq,
                IsPlaying  = (reader.Mode == Offsets.Mode_Play &&
                              reader.AudioState == Offsets.AudioState_Playing) ? 1 : 0,
                TimeMs     = reader.TimeMs,
                AudioState = reader.AudioState,
                Mode       = reader.Mode,
                MenuMods   = reader.MenuMods,
                BeatmapAR  = reader.BeatmapAR,
                BeatmapCS  = reader.BeatmapCS,
                BeatmapHP  = reader.BeatmapHP,
                BeatmapOD  = reader.BeatmapOD,
                BeatmapId  = 0,
                PlayMode   = reader.PlayMode,
                DifficultyReady  = (difficulty != null) ? 1 : 0,
                PreEmpt          = (difficulty != null) ? difficulty.PreEmpt : 0,
                HitObjectRadius  = (difficulty != null) ? difficulty.HitObjectRadius : 0f,
                GameFieldReady   = (gameField != null) ? 1 : 0,
                GameFieldRatio   = (gameField != null) ? gameField.Ratio : 0f,
                GameFieldOffsetX = (gameField != null) ? gameField.OffsetVector1.X : 0f,
                GameFieldOffsetY = (gameField != null) ? gameField.OffsetVector1.Y : 0f,
                CursorX = reader.CursorX,
                CursorY = reader.CursorY,
                OsuWindowX = osuWindowX,
                OsuWindowY = osuWindowY,
            };

            // ── 문자열 영역 빌드 (scratch 버퍼) ──
            // 각 슬롯: [int length][UTF-8 bytes... 남은 공간 0으로 채움]
            Array.Clear(stringAreaBuffer, 0, StringAreaSize);

            WriteStringSlot(stringAreaBuffer, SharedStateLayout.BeatmapFolder,      reader.BeatmapFolder);
            WriteStringSlot(stringAreaBuffer, SharedStateLayout.BeatmapOsuFilename, reader.BeatmapOsuFilename);

            // OsuInstallDir — Reconstructor가 직접 Process.MainModule.FileName로 추출할 수도 있지만,
            // writer(이쪽)가 이미 알고 있으므로 같이 전달. 실패 시 빈 문자열 → reader가 자체 추출 폴백.
            string osuDir = null;
            try { osuDir = reader.OsuInstallDir; }
            catch { osuDir = null; }
            WriteStringSlot(stringAreaBuffer, SharedStateLayout.OsuInstallDir, osuDir);

            // ── 기록 ──
            // SharedState 구조체를 헤더 영역에 직접 write (blit).
            accessor.Write<SharedState>(0, ref state);

            // 문자열 영역 write — 전체 scratch를 한 번에 복사. 단순 byte 복사라 marshal 비용 없음.
            // WriteArray는 MemoryMappedViewAccessor의 batch write.
            accessor.WriteArray(SharedStateLayout.StringAreaOffset, stringAreaBuffer, 0, StringAreaSize);

            // Sequence 다시 증가 — 같은 스냅샷이 write 완료되었음을 알림.
            // 최종 Sequence 값이 짝수이면 안정. reader는 read 전/후 Sequence가 같고 짝수면 채택.
            int finalSeq = System.Threading.Interlocked.Increment(ref sequence);
            // finalSeq가 seq+1 이어야 함 (위에서 seq, 여기서 seq+1).
            // finalSeq 값을 다시 헤더에 기록 — reader가 read 시작/끝에 같은 값인지 검증.
            accessor.Write(0, ref finalSeq);
        }

        /// <summary>
        /// scratch 버퍼 내의 지정 슬롯에 UTF-8 문자열을 기록.
        /// 형식: [4바이트 int length][UTF-8 bytes length분량][나머지 0]
        /// length는 capacity 초과 시 잘림.
        /// </summary>
        void WriteStringSlot(byte[] buf, SharedStateLayout.StringSlot slot, string value)
        {
            int offset = slot.Offset;
            int capacity = slot.Capacity;

            if (string.IsNullOrEmpty(value))
            {
                // 길이 0 — 첫 4바이트 0으로. 나머지는 이미 Array.Clear로 0.
                buf[offset + 0] = 0; buf[offset + 1] = 0; buf[offset + 2] = 0; buf[offset + 3] = 0;
                return;
            }

            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > capacity) byteCount = capacity;

            // 길이 프리픽스 (little-endian int32)
            buf[offset + 0] = (byte)(byteCount & 0xFF);
            buf[offset + 1] = (byte)((byteCount >> 8) & 0xFF);
            buf[offset + 2] = (byte)((byteCount >> 16) & 0xFF);
            buf[offset + 3] = (byte)((byteCount >> 24) & 0xFF);

            // UTF-8 인코딩 — scratch buffer의 offset+4 위치에 직접 씀
            Encoding.UTF8.GetBytes(value, 0, value.Length, buf, offset + 4);
        }

        public void Dispose()
        {
            if (accessor != null) { accessor.Dispose(); accessor = null; }
            if (mmf != null)      { mmf.Dispose();      mmf = null; }
        }
    }
}
