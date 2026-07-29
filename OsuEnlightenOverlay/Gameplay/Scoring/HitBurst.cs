using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK;
using OsuEnlightenOverlay.Gameplay.Beatmap;
using OsuEnlightenOverlay.Gameplay.Difficulty;
using OsuEnlightenOverlay.Memory;
using OsuEnlightenOverlay.Rendering;
using OsuEnlightenOverlay.Rendering.Sprites;
using OsuEnlightenOverlay.Rendering.Textures;
using OsuEnlightenOverlay.Skinning;

namespace OsuEnlightenOverlay.Gameplay.Scoring
{
    /// <summary>
    /// HitBurst — osu! stable HitObjectManager.Hit() + HitTransformationsSuccess/Fail 포팅.
    /// 메모리에서 HitObject 판정(IsHit 0→1)을 감지하여 hitburst 스프라이트를 생성.
    /// </summary>
    internal class HitBurst
    {
        SpriteManager spriteManager;
        TextureManager textureManager;

        // per-index latch: IsHit가 0→1로 변한 객체만 hitburst 생성
        // key = HitObject StartTime (고유), value = 판정 시간(ms)
        // 인덱스 기반이 아닌 StartTime 기반 — 시간 윈도우가 매 프레임 변해도 안전
        Dictionary<int, int> hitSeen = new Dictionary<int, int>();
        // StartTime → beatmapObjects 인덱스 — O(1) 조회용 (선형 검색 대신)
        Dictionary<int, int> startTimeToIndex = new Dictionary<int, int>();
        // judgements StartTime → HitObjectJudgement — 재사용 (GC 방지)
        Dictionary<int, OsuMemoryReader.HitObjectJudgement> judgementsByStartTime = new Dictionary<int, OsuMemoryReader.HitObjectJudgement>(64);

        // 활성 HitBurst 스프라이트 추적 — 별도 SpriteManager 사용 시 만료된 것을 직접 제거.
        List<pSprite> activeBursts = new List<pSprite>();
        Random reusedRandom = new Random(); // 매 프레임 new 방지

        // 이전 프레임 시간 — Retry 감지용 (시간이 크게 역행하면 retry)
        int lastTimeMs = -1;

        // HitObject 위치 캐시 (EndPosition)
        // .osu 파일에서 파싱한 HitObjectData 리스트
        List<HitObjectData> beatmapObjects;

        public HitBurst(SpriteManager sm, TextureManager tm)
        {
            spriteManager = sm;
            textureManager = tm;
        }

        /// <summary>
        /// 현재 플레이 중인 맵의 HitObject 리스트 설정.
        /// 맵 변경 시 호출.
        /// </summary>
        public void SetBeatmap(List<HitObjectData> objects)
        {
            beatmapObjects = objects;
            hitSeen.Clear();
            lastTimeMs = -1;
            // StartTime → beatmapObjects 인덱스 Dictionary 구축 — O(1) 조회용
            startTimeToIndex = new Dictionary<int, int>(objects.Count);
            for (int k = 0; k < objects.Count; k++)
            {
                if (!startTimeToIndex.ContainsKey(objects[k].StartTime))
                    startTimeToIndex[objects[k].StartTime] = k;
            }
            // 맵 변경 시 기존 HitBurst 모두 제거
            foreach (pSprite b in activeBursts)
                spriteManager.Remove(b);
            activeBursts.Clear();
        }

        /// <summary>
        /// 오버레이 재개 시 호출 — 기존 HitBurst 제거, hitSeen 리셋.
        /// 오버레이가 꺼져 있던 동안 hitSeen이 업데이트되지 않았으므로,
        /// 다시 켜질 때 과거 판정이 한번에 처리되는 것을 방지.
        /// </summary>
        public void ResetForResume()
        {
            foreach (pSprite b in activeBursts)
                spriteManager.Remove(b);
            activeBursts.Clear();
            hitSeen.Clear();
            lastTimeMs = -1;
        }

        /// <summary>
        /// Retry 감지 — 시간이 크게 역행했거나 맵이 재시작된 경우
        /// activeBursts와 hitSeen을 리셋.
        /// </summary>
        void CheckRetry(int timeMs)
        {
            if (lastTimeMs > 0 && timeMs < lastTimeMs - 2000)
            {
                // 시간이 2초 이상 역행 → retry
                foreach (pSprite b in activeBursts)
                    spriteManager.Remove(b);
                activeBursts.Clear();
                hitSeen.Clear();
            }
            lastTimeMs = timeMs;
        }

        /// <summary>
        /// 진행 중인 HitBurst를 SpriteManager에 재추가.
        /// LoadBeatmap(맵 전환)과 HOM.Update의 retry 블록(시간 역행 시)만 spriteManager.Clear()로
        /// 스프라이트를 비운다 — **매 프레임이 아니다**(F9 주석 교정). 그 직후 진행 중인 HitBurst를
        /// 다시 넣어 화면에 유지한다(맵 로드 적용 블록에서 호출).
        /// </summary>
        public void ReAddActive()
        {
            foreach (pSprite b in activeBursts)
                spriteManager.Add(b);
        }

        /// <summary>
        /// 애니메이션이 종료된 HitBurst 스프라이트를 제거.
        /// osu-stable: UpdateTransformations에서 !hasFuture && !shouldDraw → 즉시 Discard.
        /// 안전 마진 없이 Transformation 종료 시점에 즉시 제거.
        /// </summary>
        public void CleanupExpired(int timeMs)
        {
            for (int i = activeBursts.Count - 1; i >= 0; i--)
            {
                pSprite b = activeBursts[i];
                // 마지막 Transformation의 종료 시각 확인
                int endTime = 0;
                if (b.Transformations != null && b.Transformations.Count > 0)
                {
                    foreach (Transformation t in b.Transformations)
                        if (t.Time2 > endTime) endTime = t.Time2;
                }
                // Transformation 종료 시점에 activeBursts에서만 제거
                // (SpriteManager에서는 이미 Discard됨)
                if (timeMs >= endTime)
                {
                    activeBursts.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Pause 중 호출 — hitSeen만 업데이트하고 HitBurst는 생성하지 않음.
        /// Continue 후 과거 판정이 새 HitBurst로 중복 생성되는 것을 방지.
        /// osu-stable: pause 중에도 spriteManager 유지, Continue 후 이어서 재생.
        /// </summary>
        public void UpdateHitSeen(List<OsuMemoryReader.HitObjectJudgement> judgements, int timeMs)
        {
            if (judgements == null || judgements.Count == 0) return;
            if (beatmapObjects == null || beatmapObjects.Count == 0) return;
            if (timeMs <= 0) return;

            CheckRetry(timeMs);

            for (int i = 0; i < judgements.Count; i++)
            {
                var j = judgements[i];
                if (j.IsHit == 0) continue;
                if (hitSeen.ContainsKey(j.StartTime)) continue;

                // 슬라이더 완전 miss: IsHit=1, ScoreValue=0, HitValue=0
                // osu-stable은 UpdateHitObject에서 강제 Hit(slider) 호출 → Miss 반환.
                // ScoreValue=0 && HitValue=0이어도 슬라이더면 Miss로 처리.
                bool isSlider = (j.Type & 2) != 0;
                if (j.ScoreValue == 0 && j.HitValue == 0 && !isSlider) continue;

                if (j.StartTime > timeMs + 1000) continue;

                // hitSeen에만 기록 — StartTime 기반
                hitSeen[j.StartTime] = timeMs;
            }
        }

        /// <summary>
        /// 매 프레임 호출 — 메모리에서 읽은 판정 데이터로 hitburst 생성.
        /// </summary>
        /// <param name="judgements">ReadHitObjectJudgements 결과</param>
        /// <param name="timeMs">현재 AudioEngine.Time</param>
        public void Update(List<OsuMemoryReader.HitObjectJudgement> judgements, int timeMs)
        {
            if (judgements == null || judgements.Count == 0) return;
            if (beatmapObjects == null || beatmapObjects.Count == 0) return;
            // 게임 시작 전 (timeMs=0 또는 음수)에는 hitburst 생성하지 않음
            if (timeMs <= 0) return;

            // Retry 감지 — 시간 역행 시 기존 HitBurst/hitSeen 리셋
            CheckRetry(timeMs);

            judgementsByStartTime.Clear();
            for (int i = 0; i < judgements.Count; i++)
                judgementsByStartTime[judgements[i].StartTime] = judgements[i];

            int hitCount = 0, createdCount = 0, skippedSeen = 0, skippedData = 0, skippedFuture = 0;

            for (int i = 0; i < judgements.Count; i++)
            {
                var j = judgements[i];

                // IsHit가 1이 아니면 판정되지 않음
                if (j.IsHit == 0) continue;
                hitCount++;

                // 이미 hitburst 생성한 객체 — StartTime 기반 (인덱스 기반이 아님)
                if (hitSeen.ContainsKey(j.StartTime)) { skippedSeen++; continue; }

                // 스피너는 ScoreValue가 회전 중 intermediate 값(100/1100)이라 circle과 다름.
                // 판정 완료(IsHit=1)된 스피너는 무조건 300 (clear)로 처리.
                bool isSpinner = (j.Type & 8) != 0;
                bool isSlider = (j.Type & 2) != 0;

                // ScoreValue와 HitValue가 모두 0이면 잘못된 데이터 — 건너뛰기
                // 단, 슬라이더는 완전 miss 시에도 IsHit=1, ScoreValue=0, HitValue=0이 됨.
                // osu-stable: UpdateHitObject에서 강제 Hit(slider) 호출 → Miss 반환.
                if (j.ScoreValue == 0 && j.HitValue == 0 && !isSlider && !isSpinner) { skippedData++; continue; }

                // 객체의 StartTime이 현재 시간보다 미래면 아직 판정되지 않아야 함 — 건너뛰기
                // (메모리에서 IsHit=1로 미리 세팅된 객체일 수 있음)
                if (j.StartTime > timeMs + 1000) { skippedFuture++; continue; }

                // HitObject 위치 — StartTime으로 beatmapObjects에서 찾기
                Vector2 endPosition;
                int startTime;
                int endTime;
                if (!GetHitObjectInfoByStartTime(j.StartTime, out endPosition, out startTime, out endTime))
                {
                    // 조회 실패 — hitSeen에 등록하지 않음 (다음 프레임에 재시도)
                    continue;
                }

                // ScoreValue → spriteName 결정 — StartTime 기반 콤보 분석으로 Geki/Katu 계산
                // 스피너는 ScoreValue가 회전 중 intermediate 값이므로, 판정 완료 시 300(hitra)으로 강제.
                int effectiveScoreValue = isSpinner ? 300 : j.ScoreValue;
                int effectiveHitValue = isSpinner ? 1024 : ComputeComboAdditionByStartTime(
                    j.StartTime, j.ScoreValue, j.HitValue, judgementsByStartTime);
                string spriteName = GetSpriteName(effectiveScoreValue, effectiveHitValue);
                if (string.IsNullOrEmpty(spriteName))
                {
                    // spriteName 실패 — hitseen에 등록하지 않음
                    continue;
                }

                // hitseen 등록 — StartTime 기반
                hitSeen[j.StartTime] = timeMs;

                // 애니메이션 시작 시간 = 현재 시간 (timeMs)
                CreateHitBurst(spriteName, endPosition, startTime, endTime, effectiveScoreValue, timeMs, j.Type);
                createdCount++;
            }
        }

        /// <summary>
        /// StartTime으로 HitObject 위치, StartTime, EndTime 반환.
        /// 인덱스 기반이 아닌 StartTime 기반 — 시간 윈도우가 변해도 안전.
        /// </summary>
        bool GetHitObjectInfoByStartTime(int startTimeVal, out Vector2 endPosition, out int startTime, out int endTime)
        {
            endPosition = Vector2.Zero;
            startTime = 0;
            endTime = 0;

            if (beatmapObjects == null) return false;

            // O(1) Dictionary 조회 — 선형 검색 대신
            int index;
            if (!startTimeToIndex.TryGetValue(startTimeVal, out index))
                return false;
            if (index < 0 || index >= beatmapObjects.Count) return false;

            HitObjectData h = beatmapObjects[index];
            startTime = h.StartTime;
            int type = (int)h.Type;
            if (type == 0) return false;
            if ((type & (int)HitObjectType.Spinner) != 0) { endPosition = h.Position; endTime = h.EndTime; }
            else if ((type & (int)HitObjectType.Slider) != 0) { endPosition = h.BaseEndPosition; endTime = h.StartTime; }
            else { endPosition = h.Position; endTime = h.StartTime; }
            return true;
        }

        /// <summary>
        /// ScoreValue와 HitValue로 hitburst 텍스처 이름 결정.
        /// ScoreValue 기반 — 모든 HitObject 타입에서 유효.
        /// HitValue는 addition 비트(GekiAddition/KatuAddition) 확인용.
        /// </summary>
        string GetSpriteName(int scoreValue, int hitValue)
        {
            // ScoreValue로 판정 등급 결정 — 항상 300/100/50/0
            switch (scoreValue)
            {
                case 300:
                    // addition 비트 확인: GekiAddition(4)=hit300g, KatuAddition(2)=hit300k
                    if ((hitValue & 4) != 0) return "hit300g";
                    if ((hitValue & 2) != 0) return "hit300k";
                    return "hit300";
                case 100:
                    // KatuAddition(2)=hit100k
                    if ((hitValue & 2) != 0) return "hit100k";
                    return "hit100";
                case 50:
                    return "hit50";
                case 0:
                    return "hit0"; // Miss
                default:
                    return null;
            }
        }

        /// <summary>
        /// StartTime 기반 콤보 addition 계산 — .osu 파일의 NewCombo 정보 사용.
        /// judgements 인덱스가 아닌 StartTime으로 beatmapObjects에서 객체 찾기.
        /// </summary>
        int ComputeComboAdditionByStartTime(int startTimeVal, int scoreValue, int hitValue,
            Dictionary<int, OsuMemoryReader.HitObjectJudgement> judgementsByStartTime)
        {
            if (scoreValue == 0) return 0;
            if (beatmapObjects == null) return hitValue;

            // O(1) Dictionary 조회
            int objIndex;
            if (!startTimeToIndex.TryGetValue(startTimeVal, out objIndex)) return hitValue;

            HitObjectData h = beatmapObjects[objIndex];
            if (((int)h.Type & 8) != 0) return hitValue; // Spinner

            // 콤보 끝 판정
            bool endOfCombo;
            if (objIndex == beatmapObjects.Count - 1)
                endOfCombo = true;
            else
                endOfCombo = beatmapObjects[objIndex + 1].NewCombo;

            if (!endOfCombo) return hitValue;

            int comboKatu = 0;
            int comboBad = 0;
            bool backwardsMiss = false;

            if (scoreValue == 100) comboKatu++;
            else if (scoreValue == 50 || hitValue < 0) comboBad++;

            // 이전 콤보 노드들 역순 순회 — Dictionary로 O(1) 조회
            for (int k = objIndex - 1; k >= 0; k--)
            {
                if (beatmapObjects[k].NewCombo) break;

                int prevStartTime = beatmapObjects[k].StartTime;
                OsuMemoryReader.HitObjectJudgement prevJ;
                if (judgementsByStartTime.TryGetValue(prevStartTime, out prevJ))
                {
                    if (prevJ.IsHit == 0)
                    {
                        backwardsMiss = true;
                        comboBad++;
                    }
                    else if (prevJ.ScoreValue == 100)
                        comboKatu++;
                    else if (prevJ.ScoreValue == 50 || prevJ.ScoreValue == 0)
                        comboBad++;
                }
                else
                {
                    // 판정 데이터를 못 찾으면 안전하게 miss로 처리
                    backwardsMiss = true;
                }
            }

            if (comboKatu == 0 && comboBad == 0 && !backwardsMiss)
                return hitValue | 4; // GekiAddition (hit300g)
            else if (comboBad == 0 && !backwardsMiss)
                return hitValue | 2; // KatuAddition (hit300k)
            return hitValue;
        }

        /// <summary>
        /// hitburst 스프라이트 생성 + HitTransformationsSuccess/Fail 애니메이션.
        /// osu! stable HitObjectManager.Hit() 포팅 (HitObjectManager.cs:999-1010).
        /// </summary>
        void CreateHitBurst(string spriteName, Vector2 endPosition, int startTime, int endTime, int scoreValue, int timeMs, int type)
        {
            // 텍스처 로드 (pAnimation용 — LoadAll로 애니메이션 프레임 로드)
            pTexture[] textures = textureManager.LoadAll(spriteName);
            if (textures == null || textures.Length == 0)
            {
                // 단일 텍스처 폴백
                pTexture tex = textureManager.Load(spriteName);
                if (tex == null) return;
                textures = new pTexture[] { tex };
            }

            // depth — osu! stable HitObjectManager.cs:999-1010 그대로.
            // particle 시스템(pParticleBatch)은 이 오버레이에 없으므로 particle == null 경로만 해당.
            //   spinner                         → drawOrderFwdLowPrio(StartTime + 20)
            //   circle/slider (particle == null)→ drawOrderFwdPrio(EndTime - 4)
            bool isSpinner = (type & 8) != 0;
            bool isSlider = (type & 2) != 0;

            // 파티클 텍스처 로드 — osu-stable: particle{scoreAmount}, hitTexture[0].Source
            // hitburst 텍스처와 같은 소스에서 로드 (fallback 안 함)
            pTexture particleTex = null;
            if (scoreValue > 0 && textures.Length > 0)
            {
                particleTex = textureManager.Load("particle" + scoreValue, textures[0].Source);
            }

            // depth — osu-stable HitObjectManager.cs:999-1010
            // particle != null → isBelow = true → drawOrderFwdLowPrio(EndTime)
            // particle == null && circle/slider → drawOrderFwdPrio(EndTime - 4)
            // spinner → drawOrderFwdLowPrio(StartTime + 20)
            bool isBelow = particleTex != null && !isSpinner;
            float depth;
            if (isSpinner)
                depth = SpriteManager.DrawOrderFwdLowPrio(startTime + 20);
            else if (isBelow)
                depth = SpriteManager.DrawOrderFwdLowPrio(endTime);
            else
                depth = SpriteManager.DrawOrderFwdPrio(endTime - 4);

            // pAnimation 생성 — osu! stable과 동일
            pAnimation p = new pAnimation(textures, Fields.Gamefield, Origins.Centre,
                Clocks.AudioOnce, endPosition, depth, false, Color.White);
            p.LoopType = LoopTypes.LoopOnce;

            bool isMiss = scoreValue == 0;

            if (isMiss)
            {
                // HitTransformationsFail — osu! stable:
                // Fade 0→1 (time → time+HitFadeIn)
                // Scale 2→1 (time → time+HitFadeIn) (allowTransformations)
                // Rotation 0→random (time → time+HitFadeIn)
                // Rotation random→random*2 (time+HitFadeIn → time+PostEmpt+HitFadeOut, In)
                // Fade 1→0 (time+PostEmpt → time+PostEmpt+HitFadeOut)
                p.Transformations.Add(new Transformation(
                    TransformationType.Fade, 0f, 1f, timeMs, timeMs + DifficultyCalculator.HitFadeIn,
                    EasingTypes.None));

                p.Transformations.Add(new Transformation(
                    TransformationType.Scale, 2f, 1f, timeMs, timeMs + DifficultyCalculator.HitFadeIn,
                    EasingTypes.None));

                float rotation = (float)(reusedRandom.NextDouble() * 0.3 - 0.15);
                p.Transformations.Add(new Transformation(
                    TransformationType.Rotation, 0f, rotation, timeMs, timeMs + DifficultyCalculator.HitFadeIn,
                    EasingTypes.None));
                p.Transformations.Add(new Transformation(
                    TransformationType.Rotation, rotation, rotation * 2,
                    timeMs + DifficultyCalculator.HitFadeIn, timeMs + DifficultyCalculator.PostEmpt + DifficultyCalculator.HitFadeOut,
                    EasingTypes.In));

                // Movement (new layout only) — 아래로 떨어짐
                // osu-stable HitObjectManager.cs:1086:
                //   Movement(endPosition + (0,-5), endPosition + (0,40), time, time+PostEmpt+HitFadeOut, In)
                p.Transformations.Add(new Transformation(
                    TransformationType.Movement,
                    endPosition + new Vector2(0, -5), endPosition + new Vector2(0, 40),
                    timeMs, timeMs + DifficultyCalculator.PostEmpt + DifficultyCalculator.HitFadeOut,
                    EasingTypes.In));

                p.Transformations.Add(new Transformation(
                    TransformationType.Fade, 1f, 0f,
                    timeMs + DifficultyCalculator.PostEmpt, timeMs + DifficultyCalculator.PostEmpt + DifficultyCalculator.HitFadeOut,
                    EasingTypes.None));
            }
            else
            {
                // HitTransformationsSuccess — osu! stable:
                // Fade 0→1 (time → time+HitFadeIn)
                // Scale 0.6→1.1 (time → time+HitFadeIn*0.8)
                // Scale 1.1→0.9 (time+HitFadeIn → time+HitFadeIn*1.2)
                // Scale 0.9→1.0 (time+HitFadeIn → time+HitFadeIn*1.4)
                // Fade 1→0 (time+PostEmpt → time+PostEmpt+HitFadeOut)
                HitTransformationsSuccess(p, timeMs);
            }

            spriteManager.Add(p);
            activeBursts.Add(p);

            // 파티클 클론 (p2) — osu-stable HitObjectManager.cs:1022-1036
            // particle != null && isBelow && hitValue > 0
            if (particleTex != null && isBelow && scoreValue > 0)
            {
                pAnimation p2 = new pAnimation(textures, Fields.Gamefield, Origins.Centre,
                    Clocks.AudioOnce, endPosition, 1f, false, Color.White);
                p2.LoopType = LoopTypes.LoopOnce;
                p2.Additive = true;
                p2.Scale = 0.9f;

                p2.Transformations.Add(new Transformation(
                    TransformationType.Scale, 0.6f, 1.1f, timeMs, (int)(timeMs + DifficultyCalculator.HitFadeIn * 0.8)));
                p2.Transformations.Add(new Transformation(
                    TransformationType.Scale, 1.1f, 0.9f, timeMs + DifficultyCalculator.HitFadeIn, (int)(timeMs + DifficultyCalculator.HitFadeIn * 1.2)));
                p2.Transformations.Add(new Transformation(
                    TransformationType.Scale, 0.9f, 1.05f, timeMs, timeMs + DifficultyCalculator.PostEmpt + DifficultyCalculator.HitFadeOut));
                p2.Transformations.Add(new Transformation(
                    TransformationType.Fade, 0f, 0.5f, timeMs - 16, timeMs + 40, EasingTypes.Out));
                p2.Transformations.Add(new Transformation(
                    TransformationType.Fade, 0.5f, 0f, timeMs + 40, timeMs + 340));

                spriteManager.Add(p2);
                activeBursts.Add(p2);
            }

            // 파티클 폭발 — osu-stable pParticleBatch(150, 70, 0, particle, additive)
            // CPU 기반: 150개 파티클, 각각 랜덤 방향/속도/수명으로 퍼짐
            if (particleTex != null && scoreValue > 0)
            {
                CreateParticles(particleTex, endPosition, timeMs);
            }
        }

        /// <summary>
        /// 파티클 폭발 — osu-stable pParticleBatch(150, 70, 0) CPU 기반 포팅.
        /// 150개 파티클, 랜덤 방향/속도, 70ms 반경, 1200ms 지속, additive.
        /// </summary>
        void CreateParticles(pTexture particleTex, Vector2 endPosition, int timeMs)
        {
            const int particleCount = 30;
            const float baseRadius = 70f;
            const int duration = 1200;
            float depth = SpriteManager.DrawOrderFwdLowPrio(timeMs);

            // osu! stable: direction = drawScaleVector * direction * radius
            // drawScaleVector = GamefieldSpriteRatio — CS에 따라 particle 반경이 스케일됨
            float spriteRatio = spriteManager.GamefieldSpriteRatio;
            float radius = baseRadius * spriteRatio;

            Random rng = reusedRandom;
            for (int i = 0; i < particleCount; i++)
            {
                // osu-stable: time = RNG.Next(Duration/3, Duration)
                int particleLife = rng.Next(duration / 3, duration);
                double angle = rng.NextDouble() * Math.PI * 2.0;
                // 방향 × 반경 (게임필드 좌표) — osu! stable: drawScaleVector * cos/sin * radius
                float dirX = (float)Math.Cos(angle) * (float)(rng.NextDouble() * radius);
                float dirY = (float)Math.Sin(angle) * (float)(rng.NextDouble() * radius);

                // 파티클 시작 위치 = hit 위치, 끝 위치 = hit 위치 + 방향
                Vector2 startPos = endPosition;
                Vector2 endPos = endPosition + new Vector2(dirX, dirY);

                pSprite particle = new pSprite(particleTex, Fields.Gamefield, Origins.Centre,
                    Clocks.AudioOnce, startPos, depth, false, Color.White);
                particle.Additive = true;

                // Movement (시작 → 끝, 지속시간 = particleLife)
                particle.Transformations.Add(new Transformation(
                    TransformationType.Movement, startPos, endPos,
                    timeMs, timeMs + particleLife, EasingTypes.Out));

                // Fade 1→0 (수명에 비례)
                particle.Transformations.Add(new Transformation(
                    TransformationType.Fade, 1f, 0f,
                    timeMs, timeMs + particleLife, EasingTypes.In));

                spriteManager.Add(particle);
                activeBursts.Add(particle);
            }
        }

        /// <summary>
        /// HitTransformationsSuccess — osu! stable 포팅.
        /// Scale 0.6→1.1→0.9→1.0, Fade 0→1→0.
        /// </summary>
        void HitTransformationsSuccess(pAnimation p, int timeMs)
        {
            int hitFadeIn = DifficultyCalculator.HitFadeIn;
            int postEmpt = DifficultyCalculator.PostEmpt;
            int hitFadeOut = DifficultyCalculator.HitFadeOut;

            p.Transformations.Add(new Transformation(
                TransformationType.Fade, 0f, 1f, timeMs, timeMs + hitFadeIn,
                EasingTypes.None));

            // Scale 0.6→1.1 (time → time+HitFadeIn*0.8)
            p.Transformations.Add(new Transformation(
                TransformationType.Scale, 0.6f, 1.1f, timeMs, (int)(timeMs + hitFadeIn * 0.8),
                EasingTypes.None));

            // Scale 1.1→0.9 (time+HitFadeIn → time+HitFadeIn*1.2)
            p.Transformations.Add(new Transformation(
                TransformationType.Scale, 1.1f, 0.9f, timeMs + hitFadeIn, (int)(timeMs + hitFadeIn * 1.2),
                EasingTypes.None));

            // Scale 0.9→1.0 (time+HitFadeIn → time+HitFadeIn*1.4)
            p.Transformations.Add(new Transformation(
                TransformationType.Scale, 0.9f, 1f, timeMs + hitFadeIn, (int)(timeMs + hitFadeIn * 1.4),
                EasingTypes.None));

            // Fade 1→0 (time+PostEmpt → time+PostEmpt+HitFadeOut)
            p.Transformations.Add(new Transformation(
                TransformationType.Fade, 1f, 0f, timeMs + postEmpt, timeMs + postEmpt + hitFadeOut,
                EasingTypes.None));
        }

        /// <summary>
        /// 맵 변경 시 latch 리셋.
        /// </summary>
        public void Reset()
        {
            hitSeen.Clear();
            foreach (pSprite b in activeBursts)
                spriteManager.Remove(b);
            activeBursts.Clear();
        }
    }
}
