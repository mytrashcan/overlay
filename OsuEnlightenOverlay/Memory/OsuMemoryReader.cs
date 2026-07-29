using System;
using System.Collections.Generic;

namespace OsuEnlightenOverlay.Memory
{
    /// <summary>
    /// osu! stable 메모리 리더 — SIG.MD 기반.
    /// AOB 스캔으로 static field slot을 해석하고, 매 프레임 live 값을 읽음.
    /// </summary>
    public class OsuMemoryReader : IDisposable
    {
        ProcessMemory pm = new ProcessMemory();

        // 해상도/레터박싱 읽기 — WindowManager + ConfigManager Dictionary
        ResolutionReader resolution;

        // 스코어 읽기 — Ruleset → gameplayBase → scoreBase (Play 모드 전용)
        ScoreReader score;

        IntPtr timeSlot = IntPtr.Zero;
        IntPtr modeSlot = IntPtr.Zero;
        IntPtr modsSlot = IntPtr.Zero;
        IntPtr beatmapStaticAddr = IntPtr.Zero;
        IntPtr playModeSlot = IntPtr.Zero;
        List<IntPtr> cursorSlots = new List<IntPtr>();
        HashSet<long> cursorWriteSlots = new HashSet<long>(); // 연속 3개 그룹 = 커서 쓰기 함수 출력
        IntPtr cursorPositionSlot = IntPtr.Zero; // CursorPosition static slot (autopilot 커서)
        bool cursorSlotIsProvisional = false;    // 폴백으로 고른 상태 — 제대로 식별되면 승격

        public OsuMemoryReader()
        {
            resolution = new ResolutionReader(pm);
            score = new ScoreReader(pm);
        }

        public int TimeMs { get; private set; }
        public int AudioState { get; private set; }
        public int Mode { get; private set; }
        public uint MenuMods { get; private set; }
        public float CursorX { get; private set; }
        public float CursorY { get; private set; }
        public float BeatmapAR { get; private set; }
        public float BeatmapCS { get; private set; }
        public float BeatmapHP { get; private set; }
        public float BeatmapOD { get; private set; }
        public string BeatmapFolder { get; private set; }
        public string BeatmapOsuFilename { get; private set; }
        public string BeatmapDifficultyName { get; private set; }
        public int PlayMode { get; private set; }

        // HUD 편집 모드 여부 — OverlayForm이 매 프레임 세팅. Menu에서도 편집 시 해상도 갱신용 (G4).
        public bool HudEditActive;

        // ── 스코어 — ScoreReader 위임 ──
        public bool ScoreLive { get { return score.ScoreLive; } }
        public int TotalScore { get { return score.TotalScore; } }
        public int MaxCombo { get { return score.MaxCombo; } }
        public int CurrentCombo { get { return score.CurrentCombo; } }
        public ushort Count300 { get { return score.Count300; } }
        public ushort Count100 { get { return score.Count100; } }
        public ushort Count50 { get { return score.Count50; } }
        public ushort CountMiss { get { return score.CountMiss; } }

        // HUD용 추가 상태
        public double Accuracy { get { return score.Accuracy; } }
        public List<int> HitErrors { get { return score.HitErrors; } }

        // ── 레터박싱/해상도 — ResolutionReader 위임 ──
        /// <summary>실제 렌더링 너비 (WindowManager.Width)</summary>
        public int WindowWidth { get { return resolution.WindowWidth; } }
        /// <summary>실제 렌더링 높이 (WindowManager.Height, MenuHeight 제외 전)</summary>
        public int WindowHeight { get { return resolution.WindowHeight; } }
        /// <summary>레터박싱 여부 (osu! UI: "Render at native resolution")</summary>
        public bool IsLetterboxing { get { return resolution.IsLetterboxing; } }
        /// <summary>레터박스 수평 위치 (-100~100, 0=중앙)</summary>
        public int LetterboxPositionX { get { return resolution.LetterboxPositionX; } }
        /// <summary>레터박스 수직 위치 (-100~100, 0=중앙)</summary>
        public int LetterboxPositionY { get { return resolution.LetterboxPositionY; } }
        /// <summary>모니터 실제 네이티브 해상도 너비 (Win32 API)</summary>
        public int DesktopWidth { get { return resolution.DesktopWidth; } }
        /// <summary>모니터 실제 네이티브 해상도 높이 (Win32 API)</summary>
        public int DesktopHeight { get { return resolution.DesktopHeight; } }

        // 재사용 버퍼 — 매 프레임 new 할당 방지 (GC 스톨 방지)
        List<HitObjectJudgement> reusedJudgements = new List<HitObjectJudgement>(64);
        byte[] reusedHoBatch = new byte[0x118]; // hoPtr+0x10 ~ hoPtr+0x128 (IsTracking 0x120 포함)

        public bool IsOpen { get { return pm.IsOpen; } }
        public int ProcessId { get { return pm.ProcessId; } }

        /// <summary>추적 중인 osu! 프로세스가 아직 살아있는지 (G3 재접속 감지용).</summary>
        public bool IsProcessAlive() { return pm.IsProcessAlive(); }

        // G3: static slot 스캔이 완전히 성공했는지 — 재접속 시 부분 스캔 실패를 감지.
        bool staticSlotsReady = false;
        /// <summary>핸들이 열려있고 static slot 스캔까지 성공한 완전 연결 상태.</summary>
        public bool IsConnected { get { return pm.IsOpen && staticSlotsReady; } }

        /// <summary>
        /// 현재 플레이 중인 맵의 .osu 파일 전체 경로.
        /// osu! 설치 폴더/Songs/{BeatmapFolder}/{BeatmapOsuFilename}
        /// </summary>
        public string CurrentBeatmapPath
        {
            get
            {
                if (string.IsNullOrEmpty(BeatmapFolder) || string.IsNullOrEmpty(BeatmapOsuFilename))
                    return null;

                // osu! 설치 경로 — 프로세스 실행 파일 경로에서 추출
                string osuDir = OsuInstallDir;
                if (osuDir == null) return null;

                string path = System.IO.Path.Combine(osuDir, "Songs", BeatmapFolder, BeatmapOsuFilename);
                if (System.IO.File.Exists(path))
                    return path;

                return null;
            }
        }

        string cachedOsuInstallDir;

        /// <summary>
        /// osu! 설치 디렉토리 — 캐싱됨 (최초 1회만 조회).
        /// </summary>
        public string OsuInstallDir
        {
            get
            {
                if (cachedOsuInstallDir != null)
                    return cachedOsuInstallDir;
                try
                {
                    var procs = System.Diagnostics.Process.GetProcessById(ProcessId);
                    string exePath = procs.MainModule.FileName;
                    procs.Dispose();
                    cachedOsuInstallDir = System.IO.Path.GetDirectoryName(exePath);
                    return cachedOsuInstallDir;
                }
                catch
                {
                    // 폴백: 기본 경로
                    if (System.IO.Directory.Exists(@"C:\osu!"))
                        return @"C:\osu!";
                    return null;
                }
            }
        }

        public bool Initialize()
        {
            if (!pm.OpenOsu())
                return false;
            return ScanStaticSlots();
        }

        /// <summary>
        /// osu! 재접속 (G3) — 죽은 핸들을 닫고 PID 종속 캐시를 전부 리셋한 뒤 재스캔.
        /// OverlayForm이 프로세스 종료를 감지했을 때만(=연결이 끊긴 상태에서만) 호출한다.
        /// 정상 연결 경로에서는 절대 실행되지 않으므로 연결 중 동작은 그대로다.
        /// </summary>
        public bool Reconnect()
        {
            // 스캔 이전에 먼저 전부 리셋 — OpenOsu/스캔이 도중 실패해도 낡은 포인터가
            // 새 프로세스로 새어 들어가지 않도록(부분 실패 안전).
            ResetPidState();

            pm.Dispose();          // 죽은 핸들 CloseHandle → IsOpen=false
            if (!pm.OpenOsu())     // 새 osu! 탐색 + 핸들 오픈 (없으면 다음 시도까지 대기)
                return false;

            return ScanStaticSlots();
        }

        /// <summary>
        /// PID 종속 캐시 전면 초기화 — 하나라도 빠지면 새 프로세스에 옛 포인터가 남아
        /// 쓰레기/크래시가 된다. 필드 추가 시 여기도 반드시 갱신할 것 (G3).
        /// </summary>
        void ResetPidState()
        {
            staticSlotsReady = false;

            // static slot (ScanStaticSlots가 덮어쓰지만, 부분 실패 대비로 먼저 0)
            timeSlot = IntPtr.Zero;
            modeSlot = IntPtr.Zero;
            modsSlot = IntPtr.Zero;
            beatmapStaticAddr = IntPtr.Zero;
            playModeSlot = IntPtr.Zero;
            playerInstanceSlot = IntPtr.Zero; // ScanStaticSlots는 player.First!=0일 때만 덮어씀 — 필수 리셋

            // 커서 — cursorSlots는 ApplyCursorScan이 Add로 APPEND하므로 반드시 Clear
            cursorSlots.Clear();
            cursorWriteSlots.Clear();
            cursorPositionSlot = IntPtr.Zero;  // Identify는 Zero일 때만 덮어씀 — 명시 리셋 필수
            cursorSlotIsProvisional = false;
            cursorRescanLastTicks = 0;
            cursorRescanAttempts = 0;

            // 비트맵 live-read 캐시
            lastBeatmapPtr = IntPtr.Zero;
            BeatmapFolder = null;
            BeatmapOsuFilename = null;
            BeatmapDifficultyName = null;

            // HOM 캐시
            foundPlayerHomOff = -1;
            foundHomListOff = -1;
            offsetsFromAob = false;
            playerHomFromAob = false;
            offsetsFromSeed = false;
            preferredPlayerHomOff = -1;
            preferredHomListOff = -1;
            countAnomalyStreak = 0;
            cachedHoStartTimes = null;
            cachedHoEndTimes = null;
            cachedHoCount = 0;
            cachedMaxDuration = 0;
            hoCacheReady = false;
            lastBeatmapObj = IntPtr.Zero;
            lastValidatedBeatmapObj = IntPtr.Zero;
            lastValidatedHomCount = -1;
            lastValidatedFirstObject = IntPtr.Zero;
            homPrefixValidationFrames = 0;
            lastHomPrefixValidationTicks = 0;
            longObjectIndices.Clear();
            lastHomLogTag = null;
            lastHomLogTicks = 0;
            lastHomAliveTicks = 0;
            lastGoodJudgements.Clear();
            lastGoodJudgementsTicks = 0;
            fieldSuspectZeroSinceTicks = 0;
            fieldSuspectLogged = false;
            homAobRescanDone = false;
            homAobRescanAttempts = 0;
            lastHomAobRescanTicks = 0;

            // .osu 파싱/주입 캐시
            parsedHitObjects.Clear();
            parsedOsuPath = null;
            parsedStartTimes = new List<int>();
            parsedTypes = new List<int>();
            parsedOsuKey = null;

            // 설치 경로 — 새 PID는 다른 경로일 수 있음
            cachedOsuInstallDir = null;

            // 실패한 재접속 동안 오버레이가 뜨지 않도록 모드/오디오 상태를 내린다
            Mode = 0;         // Menu
            AudioState = 0;

            // 서브 리더 — 각자의 slot/인덱스/모니터 캐시 리셋
            score.ResetForReconnect();
            resolution.ResetForReconnect();
        }

        /// <summary>
        /// 기동 시 static slot 일괄 스캔.
        /// 예전에는 시그니처마다 전체 메모리를 다시 읽어 **전체 패스가 9회** 돌았다 (D1).
        /// 이제 모든 패턴을 한 배치로 넘겨 **1회 패스**로 끝낸다.
        /// </summary>
        bool ScanStaticSlots()
        {
            // time/mode/mods는 전체 매치를 모은다 — 예전엔 첫 매치를 무검증 신뢰했는데(E2),
            // 패턴이 충돌하면 조용히 쓰레기 slot을 잡았다. 전체 매치를 모아 값-도메인으로
            // 검증하고 충돌을 로깅한다. 커서가 이미 AllMatches라 스캔 패스는 어차피 전체
            // 실행영역을 훑으므로 추가 비용은 사실상 없다.
            var time = new AobScanRequest(Signatures.AudioEngineTime.Pattern, true);
            var mode = new AobScanRequest(Signatures.GameBaseMode.Pattern, true);
            var mods = new AobScanRequest(Signatures.MenuMods.Pattern, true);
            // CurrentBeatmap과 PlayMode는 패턴이 같고 OperandSkip만 다르다 — 요청 하나로 둘 다 해결
            var beatmap = new AobScanRequest(Signatures.CurrentBeatmap.Pattern, false);
            var ruleset = new AobScanRequest(Signatures.Ruleset.Pattern, false);
            var player = new AobScanRequest(Signatures.PlayerInstance.Pattern, false);
            var playerHom15 = new AobScanRequest(Signatures.PlayerHomField.Pattern, true);
            var playerHom0D = new AobScanRequest(Signatures.PlayerHomFieldEcx.Pattern, true);
            var playerHom05 = new AobScanRequest(Signatures.PlayerHomFieldEax.Pattern, true);
            var playerHom35 = new AobScanRequest(Signatures.PlayerHomFieldEsi.Pattern, true);
            var playerHomA1 = new AobScanRequest(Signatures.PlayerHomFieldA1.Pattern, true);
            var config = new AobScanRequest(Signatures.ConfigDictionary.Pattern, false);
            // 커서는 JIT가 여러 코드 사이트에 같은 코드를 방출하므로 전체 매치가 필요
            var cursor = new AobScanRequest(Signatures.CursorXY.Pattern, true);

            AobScanner.ScanBatch(pm, new[] {
                time, mode, mods, beatmap, ruleset, player,
                playerHom15, playerHom0D, playerHom05, playerHom35, playerHomA1,
                config, cursor
            });

            timeSlot = ResolveVerifiedSlot(Signatures.AudioEngineTime, time, IsPlausibleTimeSlot, "AudioEngine.Time");
            if (timeSlot == IntPtr.Zero)
                return false;

            modeSlot = ResolveVerifiedSlot(Signatures.GameBaseMode, mode, IsPlausibleModeSlot, "GameBase.Mode");
            if (modeSlot == IntPtr.Zero)
                return false;

            modsSlot = ResolveVerifiedSlot(Signatures.MenuMods, mods, IsPlausibleModsSlot, "MenuMods");
            if (modsSlot == IntPtr.Zero)
                return false;

            beatmapStaticAddr = AobScanner.ResolveSlot(pm, Signatures.CurrentBeatmap, beatmap);
            if (beatmapStaticAddr == IntPtr.Zero)
                return false;

            playModeSlot = AobScanner.ResolveSlot(pm, Signatures.PlayMode, beatmap);
            score.ApplyScan(ruleset);
            ApplyCursorScan(cursor);
            if (player.First != IntPtr.Zero)
                pm.ReadPointer(player.First + Signatures.PlayerInstance.OperandSkip, out playerInstanceSlot);
            ApplyHomFieldAob(playerHom15, playerHom0D, playerHom05, playerHom35, playerHomA1);
            resolution.ApplyScan(config);

            staticSlotsReady = timeSlot != IntPtr.Zero; // G3: 완전 스캔 성공 표식
            return staticSlotsReady;
        }

        /// <summary>
        /// Player→HOM 필드 오프셋만 JIT AOB 로 해석. list 는 AOB 하지 않음
        /// (measured 0x48 / DetectHomOffsets 휴리스틱).
        /// Play 전엔 JIT 미생성으로 실패할 수 있음 — RescanHomFieldAobInPlay 재시도.
        /// </summary>
        void ApplyHomFieldAob(
            AobScanRequest req15, AobScanRequest req0D, AobScanRequest req05,
            AobScanRequest req35, AobScanRequest reqA1)
        {
            int prevHom = foundPlayerHomOff;
            int prevList = foundHomListOff;
            bool prevFromAob = offsetsFromAob;
            bool prevPlayerHomAob = playerHomFromAob;

            offsetsFromAob = false;
            playerHomFromAob = false;

            if (playerInstanceSlot == IntPtr.Zero)
                return;

            var homVotes = new Dictionary<int, int>();

            void VotePlayerHom(AobScanRequest req, AobSignature sig, int slotOperandOff)
            {
                if (req == null) return;
                foreach (IntPtr match in req.Results)
                {
                    IntPtr slot;
                    if (!pm.ReadPointer(match + slotOperandOff, out slot) || slot != playerInstanceSlot)
                        continue;
                    if (slotOperandOff == 2)
                    {
                        byte modrm;
                        if (!pm.ReadByte(match + 1, out modrm) || !IsModRmAbsDisp32(modrm))
                            continue;
                    }
                    int disp = AobScanner.ResolveDisp8At(pm, sig, match);
                    if (disp < 0x04 || disp > 0x1FC || (disp & 3) != 0)
                        continue;
                    int n;
                    homVotes.TryGetValue(disp, out n);
                    homVotes[disp] = n + 1;
                }
            }

            VotePlayerHom(req15, Signatures.PlayerHomField, 2);
            VotePlayerHom(req0D, Signatures.PlayerHomFieldEcx, 2);
            VotePlayerHom(req05, Signatures.PlayerHomFieldEax, 2);
            VotePlayerHom(req35, Signatures.PlayerHomFieldEsi, 2);
            VotePlayerHom(reqA1, Signatures.PlayerHomFieldA1, 1);

            int bestHom = -1, bestHomVotes = 0;
            foreach (var kv in homVotes)
            {
                if (kv.Value > bestHomVotes)
                {
                    bestHomVotes = kv.Value;
                    bestHom = kv.Key;
                }
            }
            if (bestHom >= 0)
            {
                if (homVotes.ContainsKey(0x44) && homVotes[0x44] == bestHomVotes)
                    bestHom = 0x44;
                else if (homVotes.ContainsKey(Offsets.Player_HitObjectManager)
                         && homVotes[Offsets.Player_HitObjectManager] == bestHomVotes)
                    bestHom = Offsets.Player_HitObjectManager;
            }

            if (bestHom < 0)
            {
                foundPlayerHomOff = prevHom;
                foundHomListOff = prevList;
                offsetsFromAob = prevFromAob;
                playerHomFromAob = prevPlayerHomAob;
                return;
            }

            foundPlayerHomOff = bestHom;
            playerHomFromAob = true;
            // list AOB 폐기 — DetectHomOffsets 가 measured 0x48 / heuristic 으로 채움
            foundHomListOff = prevList;
            offsetsFromAob = false;
        }

        static bool IsModRmAbsDisp32(byte modrm)
        {
            return (modrm & 0xC7) == 0x05;
        }

        /// <summary>
        /// Play 중 JIT 생성된 뒤 Player→HOM AOB 재스캔 (기동 시 실패 보완).
        /// </summary>
        void RescanHomFieldAobInPlay()
        {
            if (playerInstanceSlot == IntPtr.Zero)
                return;

            var playerHom15 = new AobScanRequest(Signatures.PlayerHomField.Pattern, true);
            var playerHom0D = new AobScanRequest(Signatures.PlayerHomFieldEcx.Pattern, true);
            var playerHom05 = new AobScanRequest(Signatures.PlayerHomFieldEax.Pattern, true);
            var playerHom35 = new AobScanRequest(Signatures.PlayerHomFieldEsi.Pattern, true);
            var playerHomA1 = new AobScanRequest(Signatures.PlayerHomFieldA1.Pattern, true);
            AobScanner.ScanBatch(pm, new[] {
                playerHom15, playerHom0D, playerHom05, playerHom35, playerHomA1
            });
            ApplyHomFieldAob(playerHom15, playerHom0D, playerHom05, playerHom35, playerHomA1);
        }

        // ── E2: AOB slot 검증 (time/mode/mods) ──
        // 첫 매치를 무검증 신뢰하던 것을 방어한다. 규칙(올바른 첫 매치는 절대 밀어내지 않음):
        //  1) 첫 매치가 해석되고 검증을 통과하면 그대로 쓴다 (= 예전 동작, 회귀 0).
        //  2) 첫 매치가 검증 실패일 때만, 서로 다른 slot 중 "유일하게" 통과하는 대체가
        //     있으면 그쪽으로 교정한다.
        //  3) 그 외엔 첫 매치를 유지하되 경고를 남긴다 (조용한 실패를 로깅으로 전환).
        // 서로 다른 해석 slot이 2개 이상이면 충돌 경고 (JIT가 같은 slot을 여러 코드 사이트에
        // 방출하는 정상 다중매치는 slot이 동일하므로 경고 대상이 아니다).
        IntPtr ResolveVerifiedSlot(AobSignature sig, AobScanRequest req, Func<IntPtr, bool> isValid, string name)
        {
            IntPtr primary = req.Results.Count > 0
                ? AobScanner.ResolveSlotAt(pm, sig, req.Results[0])
                : IntPtr.Zero;

            // 서로 다른 해석 slot 수집 (충돌 가시화 + 대체 탐색)
            var distinct = new List<IntPtr>();
            foreach (IntPtr match in req.Results)
            {
                IntPtr slot = AobScanner.ResolveSlotAt(pm, sig, match);
                if (slot != IntPtr.Zero && !distinct.Contains(slot))
                    distinct.Add(slot);
            }
            if (distinct.Count > 1)
                Console.WriteLine("[AOB] " + name + " 서로 다른 slot " + distinct.Count
                    + "개 — 패턴 충돌 가능, 값 검증으로 선택");

            // 1) 첫 매치가 유효 → 그대로 (예전 동작)
            if (primary != IntPtr.Zero && isValid(primary))
                return primary;

            // 2) 첫 매치가 무효 → 유일하게 유효한 다른 slot이 있으면 교정
            IntPtr uniqueValid = IntPtr.Zero;
            int validCount = 0;
            foreach (IntPtr slot in distinct)
            {
                if (isValid(slot)) { validCount++; uniqueValid = slot; }
            }
            if (validCount == 1 && uniqueValid != primary)
            {
                Console.WriteLine("[AOB] " + name
                    + (primary == IntPtr.Zero ? " 첫 매치 해석 실패" : " 첫 매치 검증 실패")
                    + " → 유효 slot으로 교정");
                return uniqueValid;
            }

            // 3) 유일 대체 없음 → 첫 매치 유지(무검증, 예전과 동일). 첫 매치가 아예
            //    해석 안 되면 Zero 반환 → Initialize 실패(재시도 유도).
            if (primary != IntPtr.Zero)
                Console.WriteLine("[AOB] " + name + " slot 값 검증 실패 — 첫 매치 유지(무검증)");
            return primary;
        }

        // Mode(OsuModes enum): stable에 0~23까지 실제 값이 있다(Tourney=22 등 세션 지속 상태
        // 포함). 넉넉히 [0,30]로 잡아 올바른 slot은 절대 탈락시키지 않으면서 0이 아닌 쓰레기를
        // 거른다. (0은 어차피 통과하므로 상한을 넓혀도 방어력 손실 없음.)
        bool IsPlausibleModeSlot(IntPtr slot)
        {
            int v;
            return pm.ReadInt32(slot, out v) && v >= 0 && v <= 30;
        }

        // Mods bitmask: 상한을 안전하게 못 잡는다(Mirror=bit30 등 상위 비트 사용). 따라서
        // 값이 아니라 읽기 가능 여부만 구조적으로 확인한다 — 정직하게 값 검증이 아님을 명시.
        bool IsPlausibleModsSlot(IntPtr slot)
        {
            uint v;
            return pm.ReadUInt32(slot, out v);
        }

        // Time(오디오 ms) + AudioState(slot+0x30, 0/1/2). 올바른 slot은 기동 시에도 항상
        // 이 범위 안이다(정수 시간 + 유효 상태). 24시간 상한으로 포인터류 쓰레기를 배제한다.
        bool IsPlausibleTimeSlot(IntPtr slot)
        {
            int t;
            if (!pm.ReadInt32(slot, out t)) return false;
            if (t < -86400000 || t > 86400000) return false;
            int st;
            if (!pm.ReadInt32(slot + Offsets.AudioState_FromTimeSlot, out st)) return false;
            return st >= 0 && st <= 2;
        }

        /// <summary>
        /// 커서 관련 static slot AOB 스캔 — 비용이 크므로 기동 시 1회만.
        /// 실제 CursorPosition slot 선택은 IdentifyCursorPositionSlot이 담당하며,
        /// 성공할 때까지 매 프레임 재시도한다 (아래 주석 참고).
        /// </summary>
        void ScanCursorSlots()
        {
            var req = new AobScanRequest(Signatures.CursorXY.Pattern, true);
            AobScanner.ScanBatch(pm, new[] { req });
            ApplyCursorScan(req);
        }

        /// <summary>
        /// 커서 스캔 결과 적용 — 기동 시 배치 스캔과 재스캔이 공유한다.
        /// </summary>
        void ApplyCursorScan(AobScanRequest req)
        {
            List<IntPtr> matches = req.Results;

            foreach (IntPtr match in matches)
            {
                IntPtr operandAddr = match + Signatures.CursorXY.OperandSkip;
                IntPtr slot;
                if (pm.ReadPointer(operandAddr, out slot))
                    cursorSlots.Add(slot);
            }

            // 커서 쓰기 함수의 출력 slot들은 4바이트 간격으로 뭉쳐 있다(slot, slot+4, slot+8).
            // CursorPosition은 InputManager의 value type static Vector2로 다른 packed static
            // 영역에 홀로 떨어져 있다. 따라서 "이웃이 있는 slot = 쓰기 함수 출력"으로 본다.
            //
            // 이전에는 삼중쌍(s, s+4, s+8)이 모두 잡혀야 그룹으로 인정했는데, AOB 스캔이
            // 7개 중 6개만 찾으면(JIT 타이밍으로 한 코드 사이트가 아직 컴파일 전) 삼중쌍이
            // 깨져 그룹 탐지가 통째로 실패하고, 쓰기 slot이 후보로 새어 들어와 먼저 선택됐다.
            // 그러면 오버레이 커서가 인게임 커서 대신 물리 마우스를 따라간다
            // (쓰기 slot은 정수 좌표, CursorPosition은 보간된 소수 좌표).
            //
            // ±4/±8 이내 이웃 유무로 판정하면 삼중쌍이 깨져도 안전하다.
            // 실측 2개 세션(6개/7개 매치) 모두에서 정답 slot만 후보로 남는 것을 확인.
            var slotSet = new HashSet<long>();
            foreach (IntPtr s in cursorSlots)
                slotSet.Add(s.ToInt64());

            cursorWriteSlots.Clear();
            foreach (long s in slotSet)
            {
                if (slotSet.Contains(s - 8) || slotSet.Contains(s - 4) ||
                    slotSet.Contains(s + 4) || slotSet.Contains(s + 8))
                    cursorWriteSlots.Add(s);
            }

            IdentifyCursorPositionSlot();
        }

        // 커서 슬롯 재스캔 제한 — AOB 스캔은 전체 메모리를 훑어 수백 ms가 든다.
        // 렌더 스레드에서 도므로 무제한 재시도하면 계속 끊긴다.
        const long CursorRescanIntervalTicks = 2 * TimeSpan.TicksPerSecond;
        const int CursorRescanMaxAttempts = 10;
        long cursorRescanLastTicks;
        int cursorRescanAttempts;

        /// <summary>
        /// 커서 AOB 재스캔 — 슬롯 목록 자체에 CursorPosition이 없을 때만.
        /// 시간(2초)·횟수(10회) 제한. 확정되면 더 이상 호출되지 않는다.
        /// </summary>
        void TryRescanCursorSlots()
        {
            if (cursorRescanAttempts >= CursorRescanMaxAttempts) return;

            long now = DateTime.UtcNow.Ticks;
            if (now - cursorRescanLastTicks < CursorRescanIntervalTicks) return;
            cursorRescanLastTicks = now;
            cursorRescanAttempts++;

            int before = cursorSlots.Count;
            // 이전 스캔 결과를 완전히 버린다 — 폴백으로 잡아둔 주소가 새 목록에
            // 없을 수 있으므로 남겨두면 안 된다.
            cursorSlots.Clear();
            cursorWriteSlots.Clear();
            cursorPositionSlot = IntPtr.Zero;
            cursorSlotIsProvisional = false;
            ScanCursorSlots(); // 내부에서 IdentifyCursorPositionSlot까지 수행

            Console.WriteLine("[Cursor] 재스캔 " + cursorRescanAttempts + "/" + CursorRescanMaxAttempts
                + ": slots " + before + " -> " + cursorSlots.Count
                + (cursorSlotIsProvisional ? " (아직 미확정)" : " (확정)"));
        }

        /// <summary>
        /// cursorSlots 중 CursorPosition을 식별 — 값이 유효한 첫 후보를 선택.
        ///
        /// 반드시 성공할 때까지 재시도해야 한다. 기동 시점에 osu!가 메뉴에 있으면
        /// CursorPosition이 아직 갱신되지 않아 (0,0)으로 읽히고, TryReadCursor가 이를
        /// 거부해 정답 슬롯이 후보에서 탈락한다. 1회성 식별이면 그대로 폴백
        /// (cursorSlots[1])이 영구 고정되는데, 이 인덱스는 JIT 코드 배치 순서에
        /// 의존하므로 osu! 세션마다 다른 슬롯을 가리킬 수 있다 — 잘못 걸리면
        /// 커서가 (0,0)에 영구히 박힌다.
        /// </summary>
        void IdentifyCursorPositionSlot()
        {
            foreach (IntPtr slot in cursorSlots)
            {
                if (cursorWriteSlots.Contains(slot.ToInt64()))
                    continue; // 커서 쓰기 함수 출력 — skip

                IntPtr source;
                if (!pm.ReadPointer(slot, out source) || source == IntPtr.Zero)
                    continue;

                float x, y;
                if (TryReadCursor(source, out x, out y))
                {
                    cursorPositionSlot = slot;
                    cursorSlotIsProvisional = false; // 확정
                    return;
                }
            }

            // 폴백: 아직 값이 유효하지 않아 식별에 실패한 경우, 쓰기 그룹을 제외한
            // 첫 후보를 임시로 쓴다. provisional로 표시해 유효 값이 들어오는 즉시 승격된다.
            //
            // 예전에는 인덱스로 slot[1]을 집었는데, 그 인덱스는 AOB 스캔이 훑는 JIT 코드
            // 배치 순서에 의존한다 — 실측에서 slot[1]이 쓰기 슬롯인 세션이 있었다.
            if (cursorPositionSlot == IntPtr.Zero)
            {
                foreach (IntPtr slot in cursorSlots)
                {
                    if (cursorWriteSlots.Contains(slot.ToInt64()))
                        continue;
                    cursorPositionSlot = slot;
                    cursorSlotIsProvisional = true;
                    break;
                }
            }
        }

        public void RefreshLiveValues()
        {
            if (!IsOpen) return;

            int timeVal;
            if (pm.ReadInt32(timeSlot, out timeVal))
                TimeMs = timeVal;

            int audioStateVal;
            if (pm.ReadInt32(timeSlot + Offsets.AudioState_FromTimeSlot, out audioStateVal))
                AudioState = audioStateVal;

            int modeVal;
            if (pm.ReadInt32(modeSlot, out modeVal))
                Mode = modeVal;

            uint modsVal;
            if (pm.ReadUInt32(modsSlot, out modsVal))
                MenuMods = modsVal;

            // Mode: 0=Menu, 1=Edit, 2=Play, 3=Exit, 4=SelectEdit, 5=SelectPlay, 7=Rank
            // Play(2)와 SelectPlay(5)에서만 커서/비트맵/해상도 스캔
            bool needScan = Mode == Offsets.Mode_Play || Mode == Offsets.Mode_SelectPlay;

            if (needScan)
            {
                RefreshCursor();
                RefreshBeatmap();
                resolution.Refresh();
            }
            // HUD 편집 모드는 Menu(모드 0)에서도 오버레이를 표시하므로 그때도 해상도(지오메트리)를
            // 갱신해야 낡은 지오메트리로 편집하지 않는다 (G4). 커서/비트맵은 편집에 불필요하므로 제외.
            else if (HudEditActive)
                resolution.Refresh();

            if (playModeSlot != IntPtr.Zero)
            {
                int playModeVal;
                if (pm.ReadInt32(playModeSlot, out playModeVal))
                    PlayMode = playModeVal;
            }

            if (Mode == Offsets.Mode_Play && score.HasSlot)
                score.Refresh();
            else
                score.Clear();

            TryHomPlayAobRescan();
        }

        // Play 중 Player→HOM AOB 재스캔 (기동 시 JIT 미생성 보완)
        bool homAobRescanDone;
        int homAobRescanAttempts;
        long lastHomAobRescanTicks;
        const int MaxHomAobRescanAttempts = 5;
        static readonly long HomAobRescanIntervalTicks = TimeSpan.TicksPerSecond * 2;

        void TryHomPlayAobRescan()
        {
            if (playerHomFromAob)
            {
                homAobRescanDone = true;
                return;
            }
            if (homAobRescanDone)
                return;
            if (Mode != Offsets.Mode_Play)
                return;
            if (playerInstanceSlot == IntPtr.Zero)
                return;
            if (TimeMs < 2500)
                return;

            long now = DateTime.UtcNow.Ticks;
            if (lastHomAobRescanTicks != 0
                && now - lastHomAobRescanTicks < HomAobRescanIntervalTicks)
                return;
            lastHomAobRescanTicks = now;

            try
            {
                RescanHomFieldAobInPlay();
            }
            catch
            {
            }

            if (playerHomFromAob)
            {
                homAobRescanDone = true;
                return;
            }

            homAobRescanAttempts++;
            if (homAobRescanAttempts >= MaxHomAobRescanAttempts)
                homAobRescanDone = true;
        }

        void RefreshCursor()
        {
            // 슬롯이 미식별이거나 폴백(추정)이면 재식별 시도.
            // 기동 시 osu!가 메뉴에 있으면 CursorPosition이 (0,0)이라 식별이 실패하는데,
            // Play에 진입해 커서가 살아나면 여기서 정답 슬롯으로 확정/승격된다.
            if (cursorPositionSlot == IntPtr.Zero || cursorSlotIsProvisional)
            {
                // 1단계: 이미 찾아둔 슬롯들로 재식별 (싸다).
                IdentifyCursorPositionSlot();

                // 2단계: 그래도 확정 못 하면 CursorPosition 코드 사이트가 스캔 당시
                // 아직 JIT되지 않아 슬롯 목록에 아예 없는 경우다 — AOB 재스캔.
                //
                // 실측: 같은 osu!라도 세션에 따라 매치가 5~7개로 다르고, 5개인 세션엔
                // 정답 슬롯(0x...5010)이 통째로 빠져 있었다. 스캔이 기동 시 1회뿐이라
                // 그 세션은 영영 커서를 못 읽고 (0,0)에 박혔다.
                //
                // AOB 스캔은 전체 메모리를 훑어 비싸므로 시간·횟수를 제한한다.
                if (cursorPositionSlot == IntPtr.Zero || cursorSlotIsProvisional)
                    TryRescanCursorSlots();
            }

            // CursorPosition static slot만 사용 (autopilot/auto mod 인게임 커서)
            if (cursorPositionSlot == IntPtr.Zero)
            {
                CursorX = 0;
                CursorY = 0;
                return;
            }

            IntPtr source;
            if (!pm.ReadPointer(cursorPositionSlot, out source) || source == IntPtr.Zero)
            {
                CursorX = 0;
                CursorY = 0;
                return;
            }

            float x, y;
            if (TryReadCursor(source, out x, out y))
            {
                CursorX = x;
                CursorY = y;
            }
        }

        /// <summary>float이 정규(normal) 값인지 — 0은 허용, 비정규(subnormal)는 거부.</summary>
        static bool IsNormalOrZero(float v)
        {
            if (v == 0) return true;
            return Math.Abs(v) >= MinNormalFloat;
        }

        // float의 최소 정규값. 이보다 작은 0이 아닌 값은 비정규(subnormal)다.
        const float MinNormalFloat = 1.17549435E-38f;

        bool TryReadCursor(IntPtr source, out float x, out float y)
        {
            x = 0; y = 0;
            if (!pm.ReadFloat(source + Offsets.Cursor_X, out x)) return false;
            if (!pm.ReadFloat(source + Offsets.Cursor_Y, out y)) return false;

            if (float.IsNaN(x) || float.IsNaN(y)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y)) return false;
            if (Math.Abs(x) > 32768 || Math.Abs(y) > 32768) return false;
            if (x == 0 && y == 0) return false;
            if (x == 1.0f && y == 1.0f) return false;

            // 비정규(subnormal) 거부 — 실제 좌표는 항상 정규 float이다.
            //
            // 커서 쓰기 함수의 슬롯은 좌표를 int로 담고 있어서, float으로 읽으면
            // 작은 정수의 비트 패턴이 그대로 비정규값으로 보인다:
            //   int 890 -> 0x0000037A -> float 1.247156E-42
            //   int 655 -> 0x0000028F -> float 9.178505E-43
            // 이 값들은 0이 아니므로 위의 (x==0 && y==0) 검사를 통과해버렸고,
            // 그 결과 엉뚱한 슬롯이 CursorPosition으로 확정되어 커서가 (0,0)에
            // 박혔다. 정상 좌표(431 -> 0x43D78000)는 항상 정규값이므로 이 검사로
            // 두 경우를 확실히 가를 수 있다.
            if (!IsNormalOrZero(x) || !IsNormalOrZero(y)) return false;

            return true;
        }

        IntPtr lastBeatmapPtr = IntPtr.Zero;

        void RefreshBeatmap()
        {
            if (beatmapStaticAddr == IntPtr.Zero) return;

            IntPtr beatmapPtr;
            if (!pm.ReadPointer(beatmapStaticAddr, out beatmapPtr) || beatmapPtr == IntPtr.Zero)
            {
                lastBeatmapPtr = IntPtr.Zero;
                return;
            }

            // 비트맵 포인터가 같으면 AR/CS/HP/OD 및 문자열 재읽기 스킵
            // (고정값 — 곡 선택 시에만 바뀜)
            if (beatmapPtr == lastBeatmapPtr)
                return;
            lastBeatmapPtr = beatmapPtr;

            // 맵이 바뀌었을 때만 AR/CS/HP/OD 읽기.
            // ReadFloat이 실패하면 out 값이 0이라 무의미한 0으로 덮어쓰게 됨 —
            // 문자열처럼 실패 시 기존 값을 유지(건드리지 않음)해서 Reconstructor에
            // 0/쓰레기값이 새어 들어가는 것을 막는다.
            float ar, cs, hp, od;
            if (pm.ReadFloat(beatmapPtr + Offsets.Beatmap_AR, out ar))  BeatmapAR = ar;
            if (pm.ReadFloat(beatmapPtr + Offsets.Beatmap_CS, out cs))  BeatmapCS = cs;
            if (pm.ReadFloat(beatmapPtr + Offsets.Beatmap_HP, out hp))  BeatmapHP = hp;
            if (pm.ReadFloat(beatmapPtr + Offsets.Beatmap_OD, out od))  BeatmapOD = od;

            // 문자열은 맵(beatmapPtr)이 바뀔 때만 여기 도달한다 — 위 early-return이 같은
            // 포인터를 이미 걸러냈다. 예전엔 folderPtr==lastFolderPtr면 재읽기를 스킵했는데,
            // GC가 해제된 주소를 재사용하면 "다른 맵인데 옛 폴더명이 남는" 잠복 결함(E7)이 있었다.
            // 포인터 동일성 캐시를 없애고 맵 전환마다 세 문자열을 무조건 다시 읽는다 —
            // 맵 전환은 사람 조작 빈도라 비용이 무의미하다. 다만 일시적 read 실패로 null이
            // 나오면(부분 페이지 미매핑 등) 기존 값을 유지한다(덮어쓰지 않음).
            IntPtr folderPtr;
            if (pm.ReadPointer(beatmapPtr + Offsets.Beatmap_Folder, out folderPtr))
            {
                string s = pm.ReadSharpString(folderPtr);
                if (s != null) BeatmapFolder = s;
            }

            IntPtr filenamePtr;
            if (pm.ReadPointer(beatmapPtr + Offsets.Beatmap_OsuFilename, out filenamePtr))
            {
                string s = pm.ReadSharpString(filenamePtr);
                if (s != null) BeatmapOsuFilename = s;
            }

            IntPtr diffNamePtr;
            if (pm.ReadPointer(beatmapPtr + Offsets.Beatmap_DifficultyName, out diffNamePtr))
            {
                string s = pm.ReadSharpString(diffNamePtr);
                if (s != null) BeatmapDifficultyName = s;
            }
        }

        public bool IsHD { get { return (MenuMods & Offsets.Mod_HD) != 0; } }
        public bool IsHR { get { return (MenuMods & Offsets.Mod_HR) != 0; } }
        public bool IsFL { get { return (MenuMods & Offsets.Mod_FL) != 0; } }
        public bool IsDT { get { return (MenuMods & Offsets.Mod_DT) != 0; } }
        public bool IsHT { get { return (MenuMods & Offsets.Mod_HT) != 0; } }
        public bool IsNC { get { return (MenuMods & Offsets.Mod_NC) != 0; } }
        public bool IsEZ { get { return (MenuMods & Offsets.Mod_EZ) != 0; } }

        // ── HitObject 리스트 읽기 ──
        // Ruleset → HOM → hitObjects List → items 배열 → 각 HitObject

        /// <summary>
        /// HitObject 판정 데이터 — 메모리에서 읽은 값.
        /// </summary>
        public struct HitObjectJudgement
        {
            public int StartTime;
            public int EndTime;
            public int Type;
            public int ScoreValue;  // 300/100/50/0
            public byte IsHit;      // 1=판정됨
            public int HitValue;    // IncreaseScoreType
            public float FloatRotationCount; // 스피너 회전 (float, +0x10C)
            public int ScoringRotationCount;  // 스피너 회전 (int, +0xF4)
            public int RotationRequirement;    // 스피너 요구 회전수 (int, +0xF8)
            public int SpinningState;         // 스피너 상태 (0=NotStarted, 1=Started, 2=Passed)
            public byte IsTracking;           // 슬라이더 tracking 중 (0=아님, 1=tracking)
            public byte StartIsHit;          // 슬라이더 시작원 IsHit (SliderStartCircle+0x84)
            public int StartHitValue;        // 슬라이더 시작원 HitValue (IncreaseScoreType) — Arm(HitValue>0)과 동일
            public int StartScoreValue;      // 슬라이더 시작원 ScoreValue (300/100/50/0) — IncreaseScore 이후
        }

        // ── HOM 스캔 (Player.Instance + .osu 파일 검증 방식) ──
        // Player.Instance static → +오프셋 → HOM → +오프셋 → hitObjects List
        // 오프셋은 첫 스캔 시 자동 감지, 이후 고정 오프셋으로 매 프레임 빠르게 읽기

        /// <summary>
        /// 32-bit CLR 힙 포인터처럼 보이는지 검사.
        /// </summary>
        bool LooksLikeHeapPtr(uint v)
        {
            if (v == 0) return false;
            if (v == 0xFFFFFFFF) return false;
            if ((v & 3) != 0) return false;
            return v >= 0x01000000 && v < 0x80000000;
        }

        IntPtr playerInstanceSlot = IntPtr.Zero;

        // 발견된 고정 오프셋 — AOB(우선) 또는 DetectHomOffsets 휴리스틱 / 세션 시드
        int foundPlayerHomOff = -1;
        int foundHomListOff = -1;
        bool offsetsFromAob = false; // AOB로 둘 다 잡으면 count 이상치로 날리지 않음
        bool playerHomFromAob = false; // Player→HOM만 AOB여도 리셋 시 유지
        bool offsetsFromSeed = false;  // detect_ok 시드 고정 — count 이상치로 날리지 않음
        int preferredPlayerHomOff = -1;
        int preferredHomListOff = -1;
        int countAnomalyStreak = 0;  // 휴리스틱 오프셋의 count>osu+32 연속 프레임
        const int CountAnomalyResetFrames = 45;
        // 긴 객체는 longObjectIndices 로만 보완 — StartTime 창을 duration 만큼 넓히지 않음
        // (30s 캡이어도 고밀도 구간에서 win=300+ → isHit 창이 오염됨: 실측 16:48:27)

        // HitObject StartTime/EndTime 배열 캐시 (맵 로드 시 1회). StartTime/EndTime은 GC-불변이므로 안전.
        int[] cachedHoStartTimes = null;
        int[] cachedHoEndTimes = null;
        int cachedHoCount = 0;
        int cachedMaxDuration = 0;
        bool hoCacheReady = false;
        IntPtr lastBeatmapObj = IntPtr.Zero;

        struct HomSnapshot
        {
            public IntPtr Hom;
            public IntPtr List;
            public IntPtr Items;
            public IntPtr FirstObject;
            public int Count;
        }

        IntPtr lastValidatedBeatmapObj = IntPtr.Zero;
        IntPtr lastValidatedFirstObject = IntPtr.Zero;
        int lastValidatedHomCount = -1;
        int homPrefixValidationFrames = 0;
        long lastHomPrefixValidationTicks = 0;
        const int HomPrefixValidationFrameInterval = 60;
        static readonly long HomPrefixValidationTickInterval = TimeSpan.TicksPerSecond;

        // 긴 객체(슬라이더/스피너) 인덱스 — StartTime 창 밖에 있어도 EndTime 기준 활성 시 읽음
        readonly List<int> longObjectIndices = new List<int>(64);
        readonly List<int> reusedReadIndices = new List<int>(128);
        const int LongObjectMinDurationMs = 2000;

        // Step2: 전환 공백 — 마지막 성공 스냅샷 (500ms 이내 재사용)
        readonly List<HitObjectJudgement> lastGoodJudgements = new List<HitObjectJudgement>(64);
        long lastGoodJudgementsTicks = 0;
        static readonly long StaleReuseMaxTicks = TimeSpan.TicksPerMillisecond * 500;

        // Step4: 판정 필드 의심 — win>=1 & isHit=0 연속 10초
        long fieldSuspectZeroSinceTicks = 0;
        bool fieldSuspectLogged = false;
        static readonly long FieldSuspectTicks = TimeSpan.TicksPerSecond * 10;

        // Phase0: early-return 원인 로그 (태그당 rate-limit)
        string lastHomLogTag = null;
        long lastHomLogTicks = 0;
        static readonly long HomLogIntervalTicks = TimeSpan.TicksPerMillisecond * 500;
        long lastHomAliveTicks = 0;
        static readonly long HomAliveIntervalTicks = TimeSpan.TicksPerSecond * 2;

        void LogHom(string tag, int hitCount = -1, int osuCount = -1)
        {
            long now = DateTime.UtcNow.Ticks;
            if (tag == lastHomLogTag && now - lastHomLogTicks < HomLogIntervalTicks)
                return;
            lastHomLogTag = tag;
            lastHomLogTicks = now;
            Console.WriteLine("[HOM] " + tag
                + " off=" + (foundPlayerHomOff < 0 ? "-" : "0x" + foundPlayerHomOff.ToString("X"))
                + "/" + (foundHomListOff < 0 ? "-" : "0x" + foundHomListOff.ToString("X"))
                + (offsetsFromAob ? " aob" : offsetsFromSeed ? " seed" : "")
                + (hitCount >= 0 ? " hit=" + hitCount : "")
                + (osuCount >= 0 ? " osu=" + osuCount : ""));
        }

        /// <summary>
        /// early-return 시 Play 중 500ms 이내 마지막 성공 스냅샷을 돌려 Arm 엣지 놓침을 완화.
        /// </summary>
        List<HitObjectJudgement> ReturnStaleOrEmpty(string tag, int hitCount = -1, int osuCount = -1)
        {
            LogHom(tag, hitCount, osuCount);
            if (Mode == Offsets.Mode_Play && lastGoodJudgements.Count > 0
                && DateTime.UtcNow.Ticks - lastGoodJudgementsTicks < StaleReuseMaxTicks)
            {
                LogHom("stale_reuse");
                reusedJudgements.Clear();
                reusedJudgements.AddRange(lastGoodJudgements);
                return reusedJudgements;
            }
            return reusedJudgements;
        }

        void SaveGoodJudgements(List<HitObjectJudgement> src)
        {
            lastGoodJudgements.Clear();
            for (int i = 0; i < src.Count; i++)
                lastGoodJudgements.Add(src[i]);
            lastGoodJudgementsTicks = DateTime.UtcNow.Ticks;
        }

        void LockHomSeed(int playerOff, int listOff, string how)
        {
            preferredPlayerHomOff = playerOff;
            preferredHomListOff = listOff;
            offsetsFromSeed = true;
            Console.WriteLine("[HOM] seed_lock 0x" + playerOff.ToString("X")
                + "/0x" + listOff.ToString("X") + " (" + how + ")");
        }

        // .osu 파일 파싱 결과 (검증용)
        class OsuHitObject
        {
            public int StartTime;
            public int Type;
            public int RepeatCount = 1; // 슬라이더만 사용 (기본 1)
        }
        List<OsuHitObject> parsedHitObjects = new List<OsuHitObject>();
        string parsedOsuPath = null;

        // OverlayForm 주입 .osu StartTime 목록 (HOM 교차검증용).
        // reader 자체 파싱(ParseOsuFile)보다 신뢰성 높음 — OverlayForm이 이미 파싱한 결과 재사용.
        List<int> parsedStartTimes = new List<int>();
        List<int> parsedTypes = new List<int>(); // OverlayForm 주입 .osu Type 목록 (type & 0xF)
        string parsedOsuKey = null;

        /// <summary>
        /// OverlayForm이 맵 파싱 후 호출 — .osu 교차검증용 StartTime + Type 목록 주입.
        /// mapKey 가 바뀌면 검증 데이터만 갱신한다 — HOM 오프셋은 PID 가 같은 한 불변이므로
        /// 여기서 날리지 않는다.
        /// </summary>
        public void SetParsedStartTimes(List<int> startTimes, List<int> types, string mapKey)
        {
            parsedStartTimes = startTimes ?? new List<int>();
            parsedTypes = types ?? new List<int>();
            if (mapKey != parsedOsuKey)
                parsedOsuKey = mapKey;
        }

        string GetOsuFilePathFromBeatmap(IntPtr beatmapObj)
        {
            if (beatmapObj == IntPtr.Zero) return null;

            IntPtr folderPtr, filenamePtr;
            if (!pm.ReadPointer(beatmapObj + Offsets.Beatmap_Folder, out folderPtr)) return null;
            if (!pm.ReadPointer(beatmapObj + Offsets.Beatmap_OsuFilename, out filenamePtr)) return null;
            if (folderPtr == IntPtr.Zero || filenamePtr == IntPtr.Zero) return null;

            string folder = pm.ReadSharpString(folderPtr);
            string filename = pm.ReadSharpString(filenamePtr);
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(filename)) return null;

            string osuDir = OsuInstallDir;
            if (osuDir == null) return null;

            string path = System.IO.Path.Combine(osuDir, "Songs", folder, filename);
            return System.IO.File.Exists(path) ? path : null;
        }

        // .osu 파일 파싱 — [HitObjects] 섹션만 (검증용)
        void ParseOsuFile(string path)
        {
            parsedHitObjects.Clear();
            parsedOsuPath = path;

            try
            {
                string[] lines = System.IO.File.ReadAllLines(path);
                bool inHitObjects = false;

                foreach (string line in lines)
                {
                    if (line.StartsWith("[HitObjects]", StringComparison.OrdinalIgnoreCase))
                    {
                        inHitObjects = true;
                        continue;
                    }
                    if (line.StartsWith("[", StringComparison.Ordinal) && inHitObjects)
                        break;

                    if (!inHitObjects || string.IsNullOrWhiteSpace(line)) continue;

                    string[] parts = line.Split(',');
                    if (parts.Length < 4) continue;

                    int time, type;
                    if (!int.TryParse(parts[2], out time)) continue;
                    if (!int.TryParse(parts[3], out type)) continue;

                    int ty = type & 0xF;
                    int repeatCount = 1;
                    // 슬라이더(type&0xF==2)면 repeatCount 파싱: parts[6]이 slides
                    if (ty == 2 && parts.Length > 6)
                    {
                        int slides;
                        if (int.TryParse(parts[6], out slides) && slides >= 1)
                            repeatCount = slides;
                    }

                    parsedHitObjects.Add(new OsuHitObject { StartTime = time, Type = ty, RepeatCount = repeatCount });
                }
            }
            catch { }
        }

        // .osu 파싱 결과로부터 예상 HOM count (슬라이더 repeat 확장) — fallback 검증 참고용
        int CalcExpectedHomCount()
        {
            if (parsedHitObjects.Count == 0) return 0;
            int total = 0;
            foreach (var ho in parsedHitObjects)
            {
                if (ho.Type == 2) // slider
                    total += 1 + ho.RepeatCount;
                else
                    total += 1;
            }
            return total;
        }

        int GetOsuVerifyCount()
        {
            if (parsedStartTimes.Count > 0) return parsedStartTimes.Count;
            return parsedHitObjects.Count;
        }

        bool TryGetOsuVerifyAt(int index, out int startTime, out int type)
        {
            startTime = -1;
            type = -1;
            if (parsedStartTimes.Count > index)
            {
                startTime = parsedStartTimes[index];
                type = parsedTypes.Count > index ? parsedTypes[index] : -1;
                return true;
            }
            if (parsedHitObjects.Count > index)
            {
                startTime = parsedHitObjects[index].StartTime;
                type = parsedHitObjects[index].Type;
                return true;
            }
            return false;
        }

        // HOM 오프셋 탐색용 블록 읽기 버퍼 (D2) — 매 프레임 할당 방지
        byte[] homPlayerBuf = new byte[0x200];
        byte[] homCandBuf = new byte[0xA4];

        /// <summary>
        /// 블록 버퍼가 유효하면 거기서, 아니면 개별 syscall로 포인터를 읽는다.
        /// </summary>
        bool ReadPtrCached(byte[] buf, bool bufValid, IntPtr baseAddr, int off, out IntPtr val)
        {
            if (bufValid)
            {
                val = ProcessMemory.GetPointer(buf, off);
                return true;
            }
            return pm.ReadPointer(baseAddr + off, out val);
        }

        /// <summary>
        /// items[0..n) 의 StartTime/Type 이 .osu 앞 n개와 일치하는지.
        /// </summary>
        bool VerifyHomItemsPrefix(IntPtr items, int count, int prefixLen)
        {
            int osuCount = GetOsuVerifyCount();
            if (osuCount <= 0) return true; // 검증 데이터 없으면 스킵(탐지 자체는 count로)
            int n = Math.Min(prefixLen, Math.Min(count, osuCount));
            if (n < 1) return false;

            for (int i = 0; i < n; i++)
            {
                int osuSt, osuTy;
                if (!TryGetOsuVerifyAt(i, out osuSt, out osuTy)) return false;

                IntPtr ho;
                if (!pm.ReadPointer(items + 0x08 + i * 4, out ho)) return false;
                if (!LooksLikeHeapPtr((uint)ho.ToInt32())) return false;

                int st, et, ty;
                if (!pm.ReadInt32(ho + 0x10, out st)) return false;
                if (!pm.ReadInt32(ho + 0x14, out et)) return false;
                if (!pm.ReadInt32(ho + 0x18, out ty)) return false;

                if (st < 0 || st > 3600000) return false;
                if (et < st || et > 3600000) return false;
                if (ty == 0) return false;
                if (osuSt >= 0 && st != osuSt) return false;
                if (osuTy >= 0 && (ty & 0x0B) != (osuTy & 0x0B)) return false;
            }
            return true;
        }

        /// <summary>
        /// 주어진 Player→HOM / HOM→list 오프셋에서 현재 리스트 스냅샷을 읽는다.
        /// </summary>
        bool TryReadHomAt(IntPtr playerObj, int playerOff, int listOff, out HomSnapshot snapshot)
        {
            snapshot = new HomSnapshot();

            IntPtr homCand;
            if (!pm.ReadPointer(playerObj + playerOff, out homCand)) return false;
            if (!LooksLikeHeapPtr((uint)homCand.ToInt32())) return false;

            IntPtr listCand;
            if (!pm.ReadPointer(homCand + listOff, out listCand)) return false;
            if (!LooksLikeHeapPtr((uint)listCand.ToInt32())) return false;

            IntPtr items;
            if (!pm.ReadPointer(listCand + 0x04, out items)) return false;
            if (!LooksLikeHeapPtr((uint)items.ToInt32())) return false;

            int count;
            if (!pm.ReadInt32(listCand + 0x10, out count) || count < 1) return false;

            IntPtr firstObject;
            if (!pm.ReadPointer(items + 0x08, out firstObject)) return false;
            if (!LooksLikeHeapPtr((uint)firstObject.ToInt32())) return false;

            snapshot.Hom = homCand;
            snapshot.List = listCand;
            snapshot.Items = items;
            snapshot.FirstObject = firstObject;
            snapshot.Count = count;
            return true;
        }

        /// <summary>
        /// 주어진 Player→HOM / HOM→list 오프셋이 .osu 와 맞는지 검증.
        /// 검증 중 읽은 리스트 스냅샷은 호출자가 같은 프레임에 재사용한다.
        /// </summary>
        bool TryVerifyHomAt(IntPtr playerObj, int playerOff, int listOff, out HomSnapshot snapshot)
        {
            if (!TryReadHomAt(playerObj, playerOff, listOff, out snapshot))
                return false;

            int osuCount = GetOsuVerifyCount();
            int expectedExpanded = CalcExpectedHomCount();
            bool countOk = osuCount <= 0
                || Math.Abs(snapshot.Count - osuCount) <= 2
                || (expectedExpanded > 0 && Math.Abs(snapshot.Count - expectedExpanded) <= 2);
            if (!countOk) return false;

            return VerifyHomItemsPrefix(snapshot.Items, snapshot.Count, Math.Min(3, snapshot.Count));
        }

        bool ShouldValidateHomPrefix(IntPtr beatmapObj, HomSnapshot snapshot)
        {
            homPrefixValidationFrames++;
            long now = DateTime.UtcNow.Ticks;
            return beatmapObj != lastValidatedBeatmapObj
                || snapshot.Count != lastValidatedHomCount
                || snapshot.FirstObject != lastValidatedFirstObject
                || homPrefixValidationFrames >= HomPrefixValidationFrameInterval
                || now - lastHomPrefixValidationTicks >= HomPrefixValidationTickInterval;
        }

        void MarkHomPrefixValidated(IntPtr beatmapObj, HomSnapshot snapshot)
        {
            lastValidatedBeatmapObj = beatmapObj;
            lastValidatedHomCount = snapshot.Count;
            lastValidatedFirstObject = snapshot.FirstObject;
            homPrefixValidationFrames = 0;
            lastHomPrefixValidationTicks = DateTime.UtcNow.Ticks;
        }

        bool ResolveHomSnapshot(IntPtr playerObj, IntPtr beatmapObj, out HomSnapshot snapshot)
        {
            if (foundPlayerHomOff >= 0 && foundHomListOff >= 0)
            {
                if (TryReadHomAt(playerObj, foundPlayerHomOff, foundHomListOff, out snapshot))
                {
                    if (!ShouldValidateHomPrefix(beatmapObj, snapshot))
                        return true;

                    if (VerifyHomItemsPrefix(snapshot.Items, snapshot.Count, Math.Min(3, snapshot.Count)))
                    {
                        MarkHomPrefixValidated(beatmapObj, snapshot);
                        return true;
                    }
                }

                Console.WriteLine("[HOM] bad_off clear 0x" + foundPlayerHomOff.ToString("X")
                    + "/0x" + foundHomListOff.ToString("X"));
                if (preferredHomListOff == foundHomListOff)
                    preferredHomListOff = -1;
                foundHomListOff = -1;
                offsetsFromAob = false;
            }

            if (!DetectHomOffsets(playerObj, out snapshot))
                return false;

            MarkHomPrefixValidated(beatmapObj, snapshot);
            return true;
        }

        // HOM 오프셋 자동 감지 (AOB 실패 시 fallback). 시드 → 실측 상수 → 전수 스캔.
        bool DetectHomOffsets(IntPtr playerObj, out HomSnapshot snapshot)
        {
            snapshot = new HomSnapshot();
            if (playerObj == IntPtr.Zero) return false;

            int osuCount = GetOsuVerifyCount();
            int expectedExpanded = CalcExpectedHomCount();

            // 1) 세션 시드 (이전 detect_ok)
            if (foundHomListOff < 0)
            {
                if (preferredPlayerHomOff >= 0 && preferredHomListOff >= 0)
                {
                    int pOff = foundPlayerHomOff >= 0 ? foundPlayerHomOff : preferredPlayerHomOff;
                    if (TryVerifyHomAt(playerObj, pOff, preferredHomListOff, out snapshot))
                    {
                        foundPlayerHomOff = pOff;
                        foundHomListOff = preferredHomListOff;
                        offsetsFromSeed = true;
                        Console.WriteLine("[HOM] seed_hit off=0x" + foundPlayerHomOff.ToString("X")
                            + "/0x" + foundHomListOff.ToString("X") + " count=" + snapshot.Count);
                        return true;
                    }
                }

                // 2) 실측 상수 0x44/0x48 (시드 없을 때 빠른 경로)
                if (foundPlayerHomOff < 0 || foundPlayerHomOff == Offsets.Player_HitObjectManager_Measured)
                {
                    int pOff = foundPlayerHomOff >= 0
                        ? foundPlayerHomOff
                        : Offsets.Player_HitObjectManager_Measured;
                    if (TryVerifyHomAt(playerObj, pOff, Offsets.Hom_HitObjects_Measured, out snapshot))
                    {
                        foundPlayerHomOff = pOff;
                        foundHomListOff = Offsets.Hom_HitObjects_Measured;
                        LockHomSeed(foundPlayerHomOff, foundHomListOff, "measured");
                        Console.WriteLine("[HOM] detect_ok off=0x" + foundPlayerHomOff.ToString("X")
                            + "/0x" + foundHomListOff.ToString("X") + " count=" + snapshot.Count);
                        return true;
                    }
                }
            }

            bool playerHomFixed = foundPlayerHomOff >= 0;
            int offStart = playerHomFixed ? foundPlayerHomOff : 0x04;
            int offEnd = playerHomFixed ? foundPlayerHomOff : 0x1FC;

            bool playerBufOk = pm.ReadBytes(playerObj, homPlayerBuf, homPlayerBuf.Length);

            for (int off = offStart; off <= offEnd; off += 4)
            {
                IntPtr homCand;
                if (!ReadPtrCached(homPlayerBuf, playerBufOk, playerObj, off, out homCand)) continue;
                if (!LooksLikeHeapPtr((uint)homCand.ToInt32())) continue;

                bool candBufOk = pm.ReadBytes(homCand, homCandBuf, homCandBuf.Length);

                for (int listOff = 0x04; listOff <= 0xA0; listOff += 4)
                {
                    IntPtr listCand;
                    if (!ReadPtrCached(homCandBuf, candBufOk, homCand, listOff, out listCand)) continue;
                    if (!LooksLikeHeapPtr((uint)listCand.ToInt32())) continue;

                    IntPtr items;
                    if (!pm.ReadPointer(listCand + 0x04, out items)) continue;
                    if (!LooksLikeHeapPtr((uint)items.ToInt32())) continue;

                    int count;
                    if (!pm.ReadInt32(listCand + 0x10, out count)) continue;
                    if (count < 1) continue;

                    bool countOk = osuCount <= 0
                        || Math.Abs(count - osuCount) <= 2
                        || (expectedExpanded > 0 && Math.Abs(count - expectedExpanded) <= 2);
                    if (!countOk) continue;

                    int prefix = Math.Min(3, count);
                    if (!VerifyHomItemsPrefix(items, count, prefix))
                        continue;

                    IntPtr firstObject;
                    if (!pm.ReadPointer(items + 0x08, out firstObject)
                        || !LooksLikeHeapPtr((uint)firstObject.ToInt32()))
                        continue;

                    foundPlayerHomOff = off;
                    foundHomListOff = listOff;
                    LockHomSeed(off, listOff, "heuristic");
                    Console.WriteLine("[HOM] detect_ok off=0x" + off.ToString("X")
                        + "/0x" + listOff.ToString("X") + " count=" + count);
                    snapshot.Hom = homCand;
                    snapshot.List = listCand;
                    snapshot.Items = items;
                    snapshot.FirstObject = firstObject;
                    snapshot.Count = count;
                    return true;
                }

                if (playerHomFixed) break;
            }
            return false;
        }

        /// <summary>
        /// HitObject 리스트에서 판정 데이터 읽기.
        /// </summary>
        public List<HitObjectJudgement> ReadHitObjectJudgements(int maxCount, int timeRangeMs = 0)
        {
            reusedJudgements.Clear();
            List<HitObjectJudgement> result = reusedJudgements;

            IntPtr beatmapObj;
            if (!pm.ReadPointer(beatmapStaticAddr, out beatmapObj) || beatmapObj == IntPtr.Zero)
                return ReturnStaleOrEmpty("no_beatmap");

            if (beatmapObj != lastBeatmapObj)
            {
                lastBeatmapObj = beatmapObj;
                // 맵 전환 — 이전 곡 판정 스냅샷이 새 맵에 흘러가지 않게
                lastGoodJudgements.Clear();
                lastGoodJudgementsTicks = 0;
                string osuPath = GetOsuFilePathFromBeatmap(beatmapObj);
                if (osuPath != null && osuPath != parsedOsuPath)
                    ParseOsuFile(osuPath);
            }

            if (playerInstanceSlot == IntPtr.Zero)
                return ReturnStaleOrEmpty("no_player_slot");

            IntPtr playerObj;
            if (!pm.ReadPointer(playerInstanceSlot, out playerObj) || !LooksLikeHeapPtr((uint)playerObj.ToInt32()))
                return ReturnStaleOrEmpty("no_player");

            // 포인터 체인은 매 프레임 따라가되, 비싼 .osu prefix 검증은 맵/list identity
            // 변화 또는 저빈도 watchdog에서만 수행한다.
            HomSnapshot homSnapshot;
            if (!ResolveHomSnapshot(playerObj, beatmapObj, out homSnapshot))
                return ReturnStaleOrEmpty("detect_fail");

            IntPtr itemsArr = homSnapshot.Items;
            int hitCount = homSnapshot.Count;

            int osuCount = GetOsuVerifyCount();
            if (osuCount > 0 && hitCount > osuCount + 32)
            {
                // AOB/시드 오프셋은 레이아웃 불변 — 날리지 않음
                if (offsetsFromAob || offsetsFromSeed)
                {
                    LogHom("count_anomaly_aob_keep", hitCount, osuCount);
                    return result;
                }

                countAnomalyStreak++;
                if (countAnomalyStreak >= CountAnomalyResetFrames)
                {
                    if (!playerHomFromAob)
                        foundPlayerHomOff = -1;
                    foundHomListOff = -1;
                    countAnomalyStreak = 0;
                    hoCacheReady = false;
                    cachedHoStartTimes = null;
                    cachedHoEndTimes = null;
                    cachedHoCount = 0;
                    longObjectIndices.Clear();
                    LogHom("count_reset", hitCount, osuCount);
                }
                else
                    LogHom("count_anomaly", hitCount, osuCount);
                return result;
            }
            countAnomalyStreak = 0;

            // 캐시 무결성
            // ★ 맵 전환(1339→822)을 retry 축소로 오인하면 stale StartTime 캐시로
            //   새 items[] 를 읽어 win>0 / isHit=0 또는 empty_read 가 난다 (실측 로그).
            if (hoCacheReady && cachedHoCount > 0)
            {
                bool mapChanged = osuCount > 0 && Math.Abs(cachedHoCount - osuCount) > 2;
                bool countMismatch = hitCount != cachedHoCount;
                bool firstObjMismatch = false;
                if (!mapChanged && !countMismatch && hitCount > 0)
                {
                    IntPtr ho0;
                    int curSt0 = int.MinValue;
                    if (pm.ReadPointer(itemsArr + 0x08, out ho0) && ho0 != IntPtr.Zero
                        && LooksLikeHeapPtr((uint)ho0.ToInt32()))
                        pm.ReadInt32(ho0 + Offsets.HitObject_StartTime, out curSt0);
                    firstObjMismatch = curSt0 != int.MinValue && cachedHoStartTimes != null
                                       && cachedHoCount > 0 && curSt0 != cachedHoStartTimes[0];
                }

                if (mapChanged || firstObjMismatch)
                {
                    hoCacheReady = false;
                    cachedHoStartTimes = null;
                    cachedHoEndTimes = null;
                    cachedHoCount = 0;
                    longObjectIndices.Clear();
                    fieldSuspectZeroSinceTicks = 0;
                    fieldSuspectLogged = false;
                    if (mapChanged)
                        LogHom("cache_map_change", hitCount, osuCount);
                }
                else if (countMismatch && hitCount > 0 && hitCount < cachedHoCount
                         && osuCount > 0 && Math.Abs(cachedHoCount - osuCount) <= 2)
                {
                    // 같은 맵 retry 점진 충전만 유지 — 첫 StartTime 이 같아야 함.
                    // (osuCount 갱신 전 작은 맵으로 바뀌면 여기로 들어와 옛 캐시를 붙잡던 버그)
                    IntPtr ho0;
                    int curSt0 = int.MinValue;
                    if (pm.ReadPointer(itemsArr + 0x08, out ho0) && ho0 != IntPtr.Zero
                        && LooksLikeHeapPtr((uint)ho0.ToInt32()))
                        pm.ReadInt32(ho0 + Offsets.HitObject_StartTime, out curSt0);
                    bool sameMapRefill = curSt0 != int.MinValue && cachedHoStartTimes != null
                                         && cachedHoCount > 0 && curSt0 == cachedHoStartTimes[0];
                    if (!sameMapRefill)
                    {
                        hoCacheReady = false;
                        cachedHoStartTimes = null;
                        cachedHoEndTimes = null;
                        cachedHoCount = 0;
                        longObjectIndices.Clear();
                    }
                }
                else if (countMismatch)
                {
                    hoCacheReady = false;
                    cachedHoStartTimes = null;
                    cachedHoEndTimes = null;
                    cachedHoCount = 0;
                    longObjectIndices.Clear();
                }
            }

            if (!hoCacheReady)
            {
                // 부분 충전 중이고 이전 캐시도 없으면 대기 (빈 판정 폭주 방지)
                if (osuCount > 0 && hitCount < osuCount - 2)
                    return ReturnStaleOrEmpty("partial_cache", hitCount, osuCount);

                cachedHoStartTimes = new int[hitCount];
                cachedHoEndTimes = new int[hitCount];
                cachedHoCount = 0;
                longObjectIndices.Clear();
                int maxDuration = 0;

                for (int i = 0; i < hitCount; i++)
                {
                    IntPtr hoPtr;
                    if (!pm.ReadPointer(itemsArr + 0x08 + i * 4, out hoPtr)) break;
                    if (hoPtr != IntPtr.Zero && LooksLikeHeapPtr((uint)hoPtr.ToInt32()))
                    {
                        int st;
                        pm.ReadInt32(hoPtr + Offsets.HitObject_StartTime, out st);
                        cachedHoStartTimes[i] = st;
                        int et;
                        pm.ReadInt32(hoPtr + Offsets.HitObject_EndTime, out et);
                        cachedHoEndTimes[i] = et;
                        int dur = et - st;
                        if (dur > maxDuration) maxDuration = dur;
                        if (dur >= LongObjectMinDurationMs)
                            longObjectIndices.Add(i);
                    }
                    else
                    {
                        cachedHoStartTimes[i] = -1;
                        cachedHoEndTimes[i] = -1;
                    }
                    cachedHoCount++;
                }
                cachedMaxDuration = maxDuration;
                hoCacheReady = true;

                // Step4: 캐시 빌드 직후 첫 객체 StartTime/.osu 교차검증
                int osuSt0, osuTy0;
                if (cachedHoCount > 0 && cachedHoStartTimes[0] >= 0
                    && TryGetOsuVerifyAt(0, out osuSt0, out osuTy0) && osuSt0 >= 0)
                {
                    if (cachedHoStartTimes[0] != osuSt0)
                        LogHom("field_st0_mismatch", hitCount, osuCount);
                    else
                        Console.WriteLine("[HOM] field_sanity ok st0=" + osuSt0
                            + " longObjs=" + longObjectIndices.Count);
                }
            }

            // 시간 창: StartTime 은 [timeMin, timeMax] 만 (duration 확장 없음).
            // 긴 객체는 longObjectIndices 에서 EndTime 활성인 것만 추가.
            int idxStart = 0, idxEnd = cachedHoCount;
            int timeMin = 0, timeMax = int.MaxValue;
            if (timeRangeMs > 0)
            {
                timeMin = TimeMs - timeRangeMs;
                timeMax = TimeMs + 500;

                int lo = 0, hi = cachedHoCount;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (cachedHoStartTimes[mid] < timeMin)
                        lo = mid + 1;
                    else
                        hi = mid;
                }
                idxStart = Math.Max(0, lo - 2);

                lo = 0; hi = cachedHoCount;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (cachedHoStartTimes[mid] <= timeMax)
                        lo = mid + 1;
                    else
                        hi = mid;
                }
                idxEnd = Math.Min(cachedHoCount, lo + 2);
            }

            byte[] hoBatch = reusedHoBatch;
            int timeMinFilter = (timeRangeMs > 0) ? timeMin : int.MinValue;
            int idxLimit = Math.Min(idxEnd, Math.Min(cachedHoCount, hitCount));

            reusedReadIndices.Clear();
            for (int i = idxStart; i < idxLimit; i++)
            {
                if (cachedHoStartTimes[i] < 0) continue;
                if (cachedHoEndTimes[i] < timeMinFilter) continue;
                reusedReadIndices.Add(i);
            }
            // 긴 객체: StartTime 창 밖이어도 아직 진행 중이면 포함
            if (timeRangeMs > 0)
            {
                for (int li = 0; li < longObjectIndices.Count; li++)
                {
                    int i = longObjectIndices[li];
                    if (i < 0 || i >= cachedHoCount || i >= hitCount) continue;
                    if (i >= idxStart && i < idxLimit) continue; // 이미 창 안
                    int st = cachedHoStartTimes[i];
                    int et = cachedHoEndTimes[i];
                    if (st < 0) continue;
                    if (et < timeMinFilter) continue; // 이미 끝
                    if (st > timeMax) continue;       // 아직 시작 전
                    reusedReadIndices.Add(i);
                }
            }

            int readCount = 0;
            for (int ri = 0; ri < reusedReadIndices.Count; ri++)
            {
                int i = reusedReadIndices[ri];
                int startTimeVal = cachedHoStartTimes[i];
                if (startTimeVal < 0) continue;
                if (readCount >= maxCount) break;
                readCount++;

                IntPtr hoPtr;
                if (!pm.ReadPointer(itemsArr + 0x08 + i * 4, out hoPtr)) continue;
                if (hoPtr == IntPtr.Zero) continue;
                if (!LooksLikeHeapPtr((uint)hoPtr.ToInt32())) continue;

                HitObjectJudgement j = new HitObjectJudgement();
                j.StartTime = startTimeVal;

                if (!pm.ReadBytes(hoPtr + 0x10, hoBatch, 0x0C)) continue;

                j.EndTime = ProcessMemory.GetInt32(hoBatch, 0x04);
                j.Type = ProcessMemory.GetInt32(hoBatch, 0x08);

                if (j.EndTime < j.StartTime || j.EndTime > 3600000) continue;
                if (j.Type == 0) continue;

                bool isSlider = (j.Type & 2) != 0;
                bool isSpinner = (j.Type & 8) != 0;
                int readSize;
                if (isSpinner)
                    readSize = 0x100;
                else if (isSlider)
                    readSize = 0x118;
                else
                    readSize = 0x78;

                if (!pm.ReadBytes(hoPtr + 0x10, hoBatch, readSize)) continue;

                j.HitValue = ProcessMemory.GetInt32(hoBatch, 0x4C);
                j.ScoreValue = ProcessMemory.GetInt32(hoBatch, 0x70);
                j.IsHit = ProcessMemory.GetByte(hoBatch, 0x74);

                if (isSlider)
                {
                    j.IsTracking = ProcessMemory.GetByte(hoBatch, 0x110);
                    IntPtr sliderStart = ProcessMemory.GetPointer(hoBatch, 0xC0);
                    if (sliderStart != IntPtr.Zero && LooksLikeHeapPtr((uint)sliderStart.ToInt32()))
                    {
                        // StartIsHit만으로는 hit/miss 구분 불가 — timeout miss도 IsHit=1.
                        // 판정은 osu-stable HitCircle.Hit과 같이 HitValue로:
                        //   Arm(HitValue > 0). HitValue는 Hit() 안에서 IsHit와 같이 set.
                        // ScoreValue는 IncreaseScore 이후에 쓰이므로 IsHit=1인데 아직 0인
                        // 프레임이 있어, ScoreValue로 Arm하면 hit가 miss로 먼저 잠긴다.
                        byte startIsHit;
                        if (pm.ReadByte(sliderStart + Offsets.HitObject_IsHit, out startIsHit))
                            j.StartIsHit = startIsHit;
                        int startHitValue;
                        if (pm.ReadInt32(sliderStart + Offsets.HitObject_HitValue, out startHitValue))
                            j.StartHitValue = startHitValue;
                        int startScore;
                        if (pm.ReadInt32(sliderStart + Offsets.HitObject_ScoreValue, out startScore))
                            j.StartScoreValue = startScore;
                    }
                }

                if (isSpinner)
                {
                    j.FloatRotationCount = ProcessMemory.GetFloat(hoBatch, 0xFC);
                    j.ScoringRotationCount = ProcessMemory.GetInt32(hoBatch, 0xE4);
                    j.RotationRequirement = ProcessMemory.GetInt32(hoBatch, 0xE8);
                    j.SpinningState = ProcessMemory.GetInt32(hoBatch, 0xF8);
                }

                result.Add(j);
            }

            // Play 중 진단
            if (Mode == Offsets.Mode_Play && TimeMs > 0 && hoCacheReady)
            {
                int isHitN = 0;
                for (int rii = 0; rii < result.Count; rii++)
                {
                    if (result[rii].IsHit != 0) isHitN++;
                }

                long nowAlive = DateTime.UtcNow.Ticks;
                if (nowAlive - lastHomAliveTicks >= HomAliveIntervalTicks)
                {
                    lastHomAliveTicks = nowAlive;
                    Console.WriteLine("[HOM] alive t=" + TimeMs
                        + " win=" + result.Count
                        + " isHit=" + isHitN
                        + " cache=" + cachedHoCount
                        + " mem=" + hitCount
                        + " long=" + longObjectIndices.Count
                        + " off=0x" + foundPlayerHomOff.ToString("X")
                        + "/0x" + foundHomListOff.ToString("X")
                        + (result.Count > 100 ? " wide_window" : ""));
                }

                if (result.Count == 0 && reusedReadIndices.Count > 0)
                    LogHom("empty_read", hitCount, osuCount);
                else if (result.Count > 0 && isHitN == 0 && TimeMs >= 3000)
                    LogHom("no_ishit", hitCount, osuCount);

                // win>=1 에서도 감지 (예전 win=3 구간은 임계 미달로 field_suspect 미발화)
                if (result.Count >= 1 && TimeMs > 5000)
                {
                    if (isHitN == 0)
                    {
                        if (fieldSuspectZeroSinceTicks == 0)
                            fieldSuspectZeroSinceTicks = nowAlive;
                        else if (!fieldSuspectLogged
                                 && nowAlive - fieldSuspectZeroSinceTicks >= FieldSuspectTicks)
                        {
                            LogHom("field_suspect", hitCount, osuCount);
                            fieldSuspectLogged = true;
                        }
                    }
                    else
                    {
                        fieldSuspectZeroSinceTicks = 0;
                        fieldSuspectLogged = false;
                    }
                }
            }

            SaveGoodJudgements(result);
            return result;
        }

        public void Dispose()
        {
            if (pm != null)
                pm.Dispose();
        }

        /// <summary>
        /// retry 후 hook — HO StartTime 캐시와 lastGood 스냅샷을 비운다.
        /// 오프셋 시드(0x44/0x48)는 유지. 같은 맵이면 다음 프레임에 캐시 재빌드.
        /// </summary>
        public void InvalidateHoCache()
        {
            hoCacheReady = false;
            cachedHoStartTimes = null;
            cachedHoEndTimes = null;
            cachedHoCount = 0;
            cachedMaxDuration = 0;
            longObjectIndices.Clear();
            lastGoodJudgements.Clear();
            lastGoodJudgementsTicks = 0;
            fieldSuspectZeroSinceTicks = 0;
            fieldSuspectLogged = false;
        }
    }
}
