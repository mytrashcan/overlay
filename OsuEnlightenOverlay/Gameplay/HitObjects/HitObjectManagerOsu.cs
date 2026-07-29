using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK;
using OsuEnlightenOverlay.Gameplay.Beatmap;
using OsuEnlightenOverlay.Gameplay.Difficulty;
using OsuEnlightenOverlay.Graphics.Renderers;
using OsuEnlightenOverlay.Memory;
using OsuEnlightenOverlay.Rendering;
using OsuEnlightenOverlay.Rendering.Sprites;
using OsuEnlightenOverlay.Rendering.Textures;
using OsuEnlightenOverlay.Skinning;

namespace OsuEnlightenOverlay.Gameplay.HitObjects
{
    /// <summary>
    /// HitObject 관리자 — osu! stable HitObjectManager 포팅.
    /// HitObject 리스트 관리, Update, Draw.
    /// </summary>
    internal class HitObjectManagerOsu : IDisposable
    {
        // osu! stable 상수
        const int FollowLineDistance = 32;
        const int FollowLinePreEmpt = 800;
        const int FadeIn = 400;

        List<HitCircleOsu> hitCircles = new List<HitCircleOsu>();
        List<SliderOsu> sliders = new List<SliderOsu>();
        List<SpinnerOsu> spinners = new List<SpinnerOsu>();
        List<pAnimation> followPoints = new List<pAnimation>();
        SpriteManager spriteManager;
        TextureManager textureManager;
        DifficultyValues difficulty;
        BeatmapData beatmap;
        OsuGlRenderer renderer;
        MmSliderRenderer sliderRenderer;

        public HitObjectManagerOsu(SpriteManager spriteManager, TextureManager textureManager, OsuGlRenderer renderer)
        {
            this.spriteManager = spriteManager;
            this.textureManager = textureManager;
            this.renderer = renderer;

            // 슬라이더 바디 렌더러 생성
            if (renderer != null && renderer.ShaderManager != null)
                sliderRenderer = new MmSliderRenderer(renderer.ShaderManager);
        }

        /// <summary>
        /// 맵 로드 — HitObject 생성.
        /// </summary>
        public void LoadBeatmap(BeatmapData beatmap, DifficultyValues difficulty)
        {
            this.beatmap = beatmap;
            this.difficulty = difficulty;

            // 기존 HitObject 제거 — 슬라이더 바디 FBO는 GC가 못 걷으므로 먼저 해제 (B1)
            foreach (SliderOsu s in sliders)
                s.Dispose();
            hitCircles.Clear();
            sliders.Clear();
            spinners.Clear();
            spriteManager.Clear();

            // GamefieldSpriteRatio 설정 — CS 기반 스프라이트 스케일
            // SpriteRatio = SpriteDisplaySize / GamefieldSpriteRes(128)
            const int GamefieldSpriteRes = 128;
            spriteManager.GamefieldSpriteRatio = difficulty.SpriteDisplaySize / GamefieldSpriteRes;

            // Combo 색상 조회 — SkinManager에서
            List<Color> comboColours = SkinManager.GetComboColours();
            int colourCount = comboColours.Count;

            // Combo 할당 — osu! stable HitObjectManager.cs 정확 포팅
            int combo = 0;
            int comboNumber = 0;
            bool forceNew = false;
            int lastBreakPoint = -1;
            int breakCount = beatmap.Breaks.Count;

            for (int i = 0; i < beatmap.HitObjects.Count; i++)
            {
                HitObjectData h = beatmap.HitObjects[i];

                // 브레이크를 지난 첫 객체는 강제로 새 콤보 — osu! stable HitObjectManager.cs:1258-1263
                while (lastBreakPoint + 1 < breakCount &&
                       beatmap.Breaks[lastBreakPoint + 1].EndTime < h.StartTime)
                {
                    lastBreakPoint++;
                    h.NewCombo = true;
                }

                int offset = h.ComboOffset;

                if ((h.Type & HitObjectType.Spinner) != 0)
                {
                    // v≤8: 스피너는 무조건 다음 객체에 새 콤보 강제 — osu! stable HitObjectManager.cs:1267-1277
                    if (beatmap.BeatmapVersion <= 8)
                        forceNew = true;
                    else if (h.NewCombo)
                    {
                        combo += offset;
                        forceNew = true;
                    }
                }
                else if (forceNew || (h.Type & HitObjectType.NewCombo) != 0 || i == 0)
                {
                    comboNumber = 1;
                    combo += offset + 1;
                    forceNew = false;
                }
                else
                {
                    comboNumber++;
                }

                Color comboColour = colourCount > 0 ? comboColours[combo % colourCount] : Color.White;

                if ((h.Type & HitObjectType.Normal) != 0)
                {
                    HitCircleOsu circle = new HitCircleOsu(h, difficulty, textureManager, comboColour, i == 0);
                    // 콤보 번호 위치 계산용 스케일 비율 설정
                    circle.SetScaleRatios(spriteManager.GamefieldSpriteRatio, renderer.GameField.Ratio);
                    circle.ComboNumber = comboNumber;
                    hitCircles.Add(circle);
                }
                else if ((h.Type & HitObjectType.Slider) != 0)
                {
                    int colourIndex = colourCount > 0 ? combo % colourCount : 0;
                    SliderOsu slider = new SliderOsu(h, difficulty, beatmap, textureManager, comboColour, comboNumber, colourIndex, i == 0);
                    // 슬라이더 시작원 콤보 번호 위치 계산용 스케일 비율 설정
                    slider.SetScaleRatios(spriteManager.GamefieldSpriteRatio, renderer.GameField.Ratio);
                    // SetScaleRatios 후 콤보 번호 재설정 — 스프라이트 재생성 트리거
                    slider.SetComboNumber(comboNumber);
                    sliders.Add(slider);
                    // BaseEndPosition은 스택 적용 후에 설정한다 (아래 UpdateStackedPosition 루프)
                }
                else if ((h.Type & HitObjectType.Spinner) != 0)
                {
                    SpinnerOsu spinner = new SpinnerOsu(h, difficulty, textureManager, renderer.GameField);
                    spinners.Add(spinner);
                }
            }

            // 스택 계산 — osu! stable UpdateStacking (v6+)
            UpdateStacking();

            // 스택 적용 후 HitObject 위치 업데이트
            foreach (HitCircleOsu c in hitCircles)
                c.UpdateStackedPosition();
            foreach (SliderOsu s in sliders)
            {
                // 슬라이더 전체(커브·볼·틱·끝원)를 스택 위치로 이동
                s.UpdateStackedPosition();
                // HitBurst 위치 — osu-stable의 h.EndPosition은 스택이 적용된 끝 위치다.
                // 이동 후 값을 그대로 받는다. 오프셋을 여기서 따로 더하던 코드는 부호가 반대여서
                // (UpdateStacking은 basePosition '−' StackCount*stackVector) HitBurst가 반대로 밀렸다.
                s.Data.BaseEndPosition = s.HitBurstEndPosition;
            }

            // Followpoint 생성 — osu! stable AddFollowPoints
            AddFollowPoints();

            // 스프라이트 추가 — 시간 윈도우 기반 동적 추가 (Update에서 처리)
            // 초기화: 모든 HitObject를 미추가 상태로 설정
            foreach (HitCircleOsu c in hitCircles)
                c.IsSpriteAdded = false;
            foreach (SliderOsu s in sliders)
                s.IsSpriteAdded = false;
            foreach (SpinnerOsu sp in spinners)
                sp.IsSpriteAdded = false;
            foreach (pAnimation fp in followPoints)
                fp.TagNumeric = 0; // 0 = not added

            // 초기 시간 윈도우 스프라이트 추가
            sortedCircles = null; // BuildSortedLists에서 재생성
            sortedSliders = null;
            sortedSpinners = null;
            addedCircles.Clear();
            addedSliders.Clear();
            addedSpinners.Clear();
            addedFollowPoints.Clear();
            addedCirclesList.Clear();
            addedSlidersList.Clear();
            addedSpinnersList.Clear();
            addedFollowPointsList.Clear();
            sliderColoursValid = false; // 스킨 변경 시 재계산
        }

        int lastUpdateTime = -1; // Retry 감지용

        // 시간 윈도우 기반 스프라이트 동적 추가/제거
        const int SpriteWindowPast = 2000;   // 과거 2초 (잔상 유지)
        const int SpriteWindowFuture = 2000; // 미래 2초 (미리 로드)

        // 시간순 정렬된 리스트 — binary search용
        List<HitCircleOsu> sortedCircles;
        List<SliderOsu> sortedSliders;
        List<SpinnerOsu> sortedSpinners;
        // 추가된 스프라이트 추적 — HashSet로 O(1) Contains (List는 O(n)이었음)
        HashSet<HitCircleOsu> addedCircles = new HashSet<HitCircleOsu>();
        HashSet<SliderOsu> addedSliders = new HashSet<SliderOsu>();
        HashSet<SpinnerOsu> addedSpinners = new HashSet<SpinnerOsu>();
        HashSet<pAnimation> addedFollowPoints = new HashSet<pAnimation>();
        // addedXxx 리스트 역순 순회용 — HashSet는 순서가 없으므로 별도 List 유지
        // (제거 시 역순 순회 필요 — HashSet + List 동기 유지)
        List<HitCircleOsu> addedCirclesList = new List<HitCircleOsu>();
        List<SliderOsu> addedSlidersList = new List<SliderOsu>();
        List<SpinnerOsu> addedSpinnersList = new List<SpinnerOsu>();
        List<pAnimation> addedFollowPointsList = new List<pAnimation>();

        // 슬라이더 색상 캐싱 — 매 프레임 new List<Color> + GetComboColours() 호출 제거
        List<Color> cachedSliderColours;
        Color cachedSliderBorder = Color.White;
        float cachedSliderRadius = -1;
        bool sliderColoursValid = false;

        void BuildSortedLists()
        {
            sortedCircles = new List<HitCircleOsu>(hitCircles);
            sortedCircles.Sort((a, b) => a.Data.StartTime.CompareTo(b.Data.StartTime));
            sortedSliders = new List<SliderOsu>(sliders);
            sortedSliders.Sort((a, b) => a.Data.StartTime.CompareTo(b.Data.StartTime));
            sortedSpinners = new List<SpinnerOsu>(spinners);
            sortedSpinners.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        }

        // binary search: startTime >= target 인 첫 인덱스
        static int LowerBound<T>(List<T> list, Func<T, int> getStart, int target)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (getStart(list[mid]) < target)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        // binary search: startTime <= target 인 마지막 인덱스 + 1
        static int UpperBound<T>(List<T> list, Func<T, int> getStart, int target)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (getStart(list[mid]) <= target)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        void UpdateSpriteWindow(int timeMs)
        {
            int minTime = timeMs - SpriteWindowPast;
            int maxTime = timeMs + SpriteWindowFuture;

            if (sortedCircles == null) BuildSortedLists();

            // HitCircles — binary search로 윈도우 내 객체만 순회
            int cStart = LowerBound(sortedCircles, c => c.Data.StartTime, minTime - DifficultyCalculator.FadeOut - 100);
            int cEnd = UpperBound(sortedCircles, c => c.Data.StartTime, maxTime);
            for (int i = cStart; i < cEnd; i++)
            {
                HitCircleOsu c = sortedCircles[i];
                int startTime = c.Data.StartTime;
                int endTime = startTime + difficulty.HitWindow50 + DifficultyCalculator.FadeOut;
                bool inWindow = startTime <= maxTime && endTime >= minTime;
                if (inWindow && !c.IsSpriteAdded)
                {
                    c.AddToSpriteManager(spriteManager);
                    c.IsSpriteAdded = true;
                }
                else if (!inWindow && c.IsSpriteAdded)
                {
                    c.RemoveFromSpriteManager(spriteManager);
                    c.IsSpriteAdded = false;
                }
            }
            // 윈도우 밖 제거 — addedCirclesList 역순 순회 (HashSet + List 동기 유지)
            for (int i = addedCirclesList.Count - 1; i >= 0; i--)
            {
                HitCircleOsu c = addedCirclesList[i];
                int startTime = c.Data.StartTime;
                int endTime = startTime + difficulty.HitWindow50 + DifficultyCalculator.FadeOut;
                if (startTime > maxTime || endTime < minTime)
                {
                    if (c.IsSpriteAdded)
                    {
                        c.RemoveFromSpriteManager(spriteManager);
                        c.IsSpriteAdded = false;
                    }
                    addedCircles.Remove(c);
                    addedCirclesList.RemoveAt(i);
                }
            }
            // 윈도우 내에서 새로 추가된 것을 addedCircles에 등록 (HashSet O(1) Contains)
            for (int i = cStart; i < cEnd; i++)
            {
                HitCircleOsu c = sortedCircles[i];
                if (c.IsSpriteAdded && addedCircles.Add(c)) // Add returns true if new
                    addedCirclesList.Add(c);
            }

            // Sliders — 전체 순회 (긴 슬라이더는 binary search가 StartTime 기준이라 놓침)
            for (int i = 0; i < sortedSliders.Count; i++)
            {
                SliderOsu s = sortedSliders[i];
                int startTime = s.Data.StartTime;
                int endTime = s.VirtualEndTime + DifficultyCalculator.FadeOut;
                bool inWindow = startTime <= maxTime && endTime >= minTime;
                if (inWindow && !s.IsSpriteAdded)
                {
                    s.AddToSpriteManager(spriteManager, timeMs);
                    s.IsSpriteAdded = true;
                }
                else if (!inWindow && s.IsSpriteAdded)
                {
                    s.RemoveFromSpriteManager(spriteManager);
                    s.IsSpriteAdded = false;
                }
            }
            for (int i = addedSlidersList.Count - 1; i >= 0; i--)
            {
                SliderOsu s = addedSlidersList[i];
                int startTime = s.Data.StartTime;
                int endTime = s.VirtualEndTime + DifficultyCalculator.FadeOut;
                if (startTime > maxTime || endTime < minTime)
                {
                    if (s.IsSpriteAdded)
                    {
                        s.RemoveFromSpriteManager(spriteManager);
                        s.IsSpriteAdded = false;
                    }
                    addedSliders.Remove(s);
                    addedSlidersList.RemoveAt(i);
                }
            }
            for (int i = 0; i < sortedSliders.Count; i++)
            {
                SliderOsu s = sortedSliders[i];
                if (s.IsSpriteAdded && addedSliders.Add(s))
                    addedSlidersList.Add(s);
            }

            // Spinners — 전체 순회 (스피너는 맵당 몇 개 안 됨, binary search는 긴 스피너를 놓침)
            for (int i = 0; i < sortedSpinners.Count; i++)
            {
                SpinnerOsu sp = sortedSpinners[i];
                int startTime = sp.StartTime;
                int endTime = sp.EndTime + DifficultyCalculator.FadeOut;
                bool inWindow = startTime <= maxTime && endTime >= minTime;
                if (inWindow && !sp.IsSpriteAdded)
                {
                    sp.ResetState();
                    sp.AddToSpriteManager(spriteManager, 0, 0, 0, 0, 0, 0);
                    sp.IsSpriteAdded = true;
                }
                else if (!inWindow && sp.IsSpriteAdded)
                {
                    sp.RemoveFromSpriteManager(spriteManager);
                    sp.IsSpriteAdded = false;
                }
            }
            for (int i = addedSpinnersList.Count - 1; i >= 0; i--)
            {
                SpinnerOsu sp = addedSpinnersList[i];
                int startTime = sp.StartTime;
                int endTime = sp.EndTime + DifficultyCalculator.FadeOut;
                if (startTime > maxTime || endTime < minTime)
                {
                    if (sp.IsSpriteAdded)
                    {
                        sp.RemoveFromSpriteManager(spriteManager);
                        sp.IsSpriteAdded = false;
                    }
                    addedSpinners.Remove(sp);
                    addedSpinnersList.RemoveAt(i);
                }
            }
            for (int i = 0; i < sortedSpinners.Count; i++)
            {
                SpinnerOsu sp = sortedSpinners[i];
                if (sp.IsSpriteAdded && addedSpinners.Add(sp))
                    addedSpinnersList.Add(sp);
            }

            // FollowPoints — binary search
            int fpStart = LowerBound(followPoints, fp => fp.StartTime, minTime);
            int fpEnd = UpperBound(followPoints, fp => fp.StartTime, maxTime);
            for (int i = fpStart; i < fpEnd; i++)
            {
                pAnimation fp = followPoints[i];
                int fpStartTime = fp.StartTime;
                int fpEndTime = fp.EndTime;
                bool inWindow = fpStartTime <= maxTime && fpEndTime >= minTime;
                if (inWindow && fp.TagNumeric == 0)
                {
                    spriteManager.Add(fp);
                    fp.TagNumeric = 1;
                }
                else if (!inWindow && fp.TagNumeric == 1)
                {
                    spriteManager.Remove(fp);
                    fp.TagNumeric = 0;
                }
            }
            for (int i = addedFollowPointsList.Count - 1; i >= 0; i--)
            {
                pAnimation fp = addedFollowPointsList[i];
                if (fp.StartTime > maxTime || fp.EndTime < minTime)
                {
                    if (fp.TagNumeric == 1)
                    {
                        spriteManager.Remove(fp);
                        fp.TagNumeric = 0;
                    }
                    addedFollowPoints.Remove(fp);
                    addedFollowPointsList.RemoveAt(i);
                }
            }
            for (int i = fpStart; i < fpEnd; i++)
            {
                pAnimation fp = followPoints[i];
                if (fp.TagNumeric == 1 && addedFollowPoints.Add(fp))
                    addedFollowPointsList.Add(fp);
            }
        }

        /// <summary>
        /// Followpoint 생성 — osu! stable HitObjectManager.AddFollowPoints 포팅.
        /// 연속하는 HitObject 사이에 followpoint 화살표 배치.
        /// NewCombo, Spinner 건너뜀.
        /// </summary>
        void AddFollowPoints()
        {
            followPoints.Clear();

            // 통합 HitObject 리스트 생성 (시간순 정렬)
            List<FollowPointEntry> entries = new List<FollowPointEntry>();
            foreach (HitCircleOsu c in hitCircles)
            {
                HitObjectData d = c.Data;
                entries.Add(new FollowPointEntry {
                    position = d.Position,
                    endPosition = d.Position,
                    startTime = d.StartTime,
                    endTime = d.StartTime,
                    newCombo = d.NewCombo,
                    isSpinner = (d.Type & HitObjectType.Spinner) != 0
                });
            }
            foreach (SliderOsu s in sliders)
            {
                HitObjectData d = s.Data;
                entries.Add(new FollowPointEntry {
                    position = d.Position,
                    endPosition = s.EndPosition,
                    startTime = d.StartTime,
                    endTime = s.VirtualEndTime,
                    newCombo = d.NewCombo,
                    isSpinner = false
                });
            }
            entries.Sort((a, b) => a.startTime.CompareTo(b.startTime));

            pTexture[] fpTextures = textureManager.LoadAll("followpoint");
            if (fpTextures == null || fpTextures.Length == 0) return;

            for (int i = 1; i < entries.Count; i++)
            {
                FollowPointEntry prev = entries[i - 1];
                FollowPointEntry curr = entries[i];

                // NewCombo이거나 Spinner이면 건너뜀
                if (curr.newCombo || prev.isSpinner || curr.isSpinner)
                    continue;

                Vector2 pos1 = prev.endPosition;
                int time1 = prev.endTime;
                Vector2 pos2 = curr.position;
                int time2 = curr.startTime;

                float distance = Vector2.Distance(pos1, pos2);
                Vector2 distanceVector = pos2 - pos1;
                int length = time2 - time1;

                float angle = (float)Math.Atan2(pos2.Y - pos1.Y, pos2.X - pos1.X);

                for (float j = FollowLineDistance * 1.5f; j < distance - FollowLineDistance; j += FollowLineDistance)
                {
                    float fraction = j / distance;
                    Vector2 posStart = pos1 + (fraction - 0.1f) * distanceVector;
                    Vector2 pos = pos1 + fraction * distanceVector;
                    int fadein = (int)(time1 + fraction * length) - FollowLinePreEmpt;
                    int fadeout = (int)(time1 + fraction * length);

                    pAnimation dot = new pAnimation(fpTextures, Fields.Gamefield, Origins.Centre, Clocks.Audio,
                        pos, 0, false, Color.White);
                    dot.SetFramerateFromSkin();
                    dot.Alpha = 0f; // fade in 전에는 보이지 않음

                    // Fade in: 0 → 1
                    dot.Transformations.Add(new Transformation(
                        TransformationType.Fade, 0f, 1f, fadein, fadein + FadeIn, EasingTypes.None));

                    // Scale + Movement: default 스킨에서만 적용 (H15)
                    // stable HitObjectManager.cs:1887-1891:
                    //   if (SkinManager.IsDefault && GameBase.NewGraphicsAvailable) {
                    //       Scale 1.5→1 Out; Movement posStart→pos Out }
                    // NewGraphicsAvailable는 이 오버레이에서 항상 true이므로 IsDefault만 게이트한다.
                    // posStart(line 498)는 이전엔 계산만 하고 안 쓰던 데드코드였다.
                    if (SkinManager.IsDefault)
                    {
                        dot.Transformations.Add(new Transformation(
                            TransformationType.Scale, 1.5f, 1f, fadein, fadein + FadeIn, EasingTypes.Out));
                        dot.Transformations.Add(new Transformation(
                            TransformationType.Movement, posStart, pos, fadein, fadein + FadeIn, EasingTypes.Out));
                    }

                    // Fade out: 1 → 0
                    dot.Transformations.Add(new Transformation(
                        TransformationType.Fade, 1f, 0f, fadeout, fadeout + FadeIn, EasingTypes.None));

                    dot.Rotation = angle;

                    // ComputeTimeRange 호출 — binary search에서 StartTime/EndTime 사용
                    dot.ComputeTimeRange();

                    followPoints.Add(dot);
                }
            }
        }

        struct FollowPointEntry
        {
            public Vector2 position;
            public Vector2 endPosition;
            public int startTime;
            public int endTime;
            public bool newCombo;
            public bool isSpinner;
        }

        /// <summary>
        /// 스택 계산 — osu! stable HitObjectManager.UpdateStacking (v6+) 정확 포팅.
        /// 겹치는 HitObject의 위치를 StackOffset만큼 이동.
        /// </summary>
        void UpdateStacking()
        {
            // 통합 HitObject 리스트 (시간순)
            List<StackEntry> entries = new List<StackEntry>();
            foreach (HitCircleOsu c in hitCircles)
            {
                HitObjectData d = c.Data;
                entries.Add(new StackEntry
                {
                    data = d,
                    isSpinner = (d.Type & HitObjectType.Spinner) != 0,
                    isSlider = (d.Type & HitObjectType.Slider) != 0,
                    isCircle = (d.Type & HitObjectType.Normal) != 0,
                    basePosition = d.BasePosition,
                    baseEndPosition = d.BasePosition,
                    startTime = d.StartTime,
                    endTime = d.EndTime
                });
            }
            foreach (SliderOsu s in sliders)
            {
                HitObjectData d = s.Data;
                entries.Add(new StackEntry
                {
                    data = d,
                    isSpinner = false,
                    isSlider = true,
                    isCircle = false,
                    basePosition = d.BasePosition,
                    baseEndPosition = s.EndPosition,
                    startTime = d.StartTime,
                    endTime = s.VirtualEndTime
                });
            }
            foreach (SpinnerOsu sp in spinners)
            {
                HitObjectData d = sp.Data;
                entries.Add(new StackEntry
                {
                    data = d,
                    isSpinner = true,
                    isSlider = false,
                    isCircle = false,
                    basePosition = d.BasePosition,
                    baseEndPosition = d.BasePosition,
                    startTime = d.StartTime,
                    endTime = d.EndTime
                });
            }
            entries.Sort((a, b) => a.startTime.CompareTo(b.startTime));

            int count = entries.Count;
            if (count == 0) return;

            const int STACK_LENIENCE = 3;
            Vector2 stackVector = new Vector2(difficulty.StackOffset, difficulty.StackOffset);
            float stackThreshold = difficulty.PreEmpt * beatmap.StackLeniency;

            // StackCount 초기화
            for (int i = 0; i < count; i++)
                entries[i].data.StackCount = 0;

            // Extend end index
            int extendedEndIndex = count - 1;
            for (int i = count - 1; i >= 0; i--)
            {
                int stackBaseIndex = i;
                for (int n = stackBaseIndex + 1; n < count; n++)
                {
                    StackEntry stackBase = entries[stackBaseIndex];
                    if (stackBase.isSpinner) break;

                    StackEntry objectN = entries[n];
                    if (objectN.isSpinner) continue;

                    if (objectN.startTime - stackBase.endTime > stackThreshold)
                        break;

                    if (Vector2.Distance(stackBase.basePosition, objectN.basePosition) < STACK_LENIENCE ||
                        (stackBase.isSlider && Vector2.Distance(stackBase.baseEndPosition, objectN.basePosition) < STACK_LENIENCE))
                    {
                        stackBaseIndex = n;
                        objectN.data.StackCount = 0;
                    }
                }

                if (stackBaseIndex > extendedEndIndex)
                {
                    extendedEndIndex = stackBaseIndex;
                    if (extendedEndIndex == count - 1)
                        break;
                }
            }

            // Reverse pass
            int extendedStartIndex = 0;
            for (int i = extendedEndIndex; i > 0; i--)
            {
                int n = i;
                StackEntry objectI = entries[i];

                if (objectI.data.StackCount != 0 || objectI.isSpinner) continue;

                if (objectI.isCircle)
                {
                    while (--n >= 0)
                    {
                        StackEntry objectN = entries[n];
                        if (objectN.isSpinner) continue;
                        if (objectI.startTime - objectN.endTime > stackThreshold)
                            break;

                        if (n < extendedStartIndex)
                        {
                            objectN.data.StackCount = 0;
                            extendedStartIndex = n;
                        }

                        if (objectN.isSlider && Vector2.Distance(objectN.baseEndPosition, objectI.basePosition) < STACK_LENIENCE)
                        {
                            int offset = objectI.data.StackCount - objectN.data.StackCount + 1;
                            for (int j = n + 1; j <= i; j++)
                            {
                                if (Vector2.Distance(objectN.baseEndPosition, entries[j].basePosition) < STACK_LENIENCE)
                                    entries[j].data.StackCount -= offset;
                            }
                            break;
                        }

                        if (Vector2.Distance(objectN.basePosition, objectI.basePosition) < STACK_LENIENCE)
                        {
                            objectN.data.StackCount = objectI.data.StackCount + 1;
                            objectI = objectN;
                        }
                    }
                }
                else if (objectI.isSlider)
                {
                    while (--n >= 0)
                    {
                        StackEntry objectN = entries[n];
                        if (objectN.isSpinner) continue;
                        if (objectI.startTime - objectN.startTime > stackThreshold)
                            break;

                        if (Vector2.Distance(objectN.baseEndPosition, objectI.basePosition) < STACK_LENIENCE)
                        {
                            objectN.data.StackCount = objectI.data.StackCount + 1;
                            objectI = objectN;
                        }
                    }
                }
            }

            // 스택 오프셋 적용 — osu! stable HitObjectManager.cs:1761-1765는 범위 내 전 객체에
            // 무조건 ModifyPosition을 건다 (H20). StackCount==0을 건너뛰면 Position이 낡은 값으로
            // 남을 수 있고, UpdateStackedPosition이 쓰는 `Position - BasePosition` 불변식이 깨진다.
            for (int i = 0; i < count; i++)
            {
                StackEntry e = entries[i];
                e.data.Position = e.basePosition - e.data.StackCount * stackVector;
            }
        }

        struct StackEntry
        {
            public HitObjectData data;
            public bool isSpinner;
            public bool isSlider;
            public bool isCircle;
            public Vector2 basePosition;
            public Vector2 baseEndPosition;
            public int startTime;
            public int endTime;
        }

        /// <summary>
        /// difficulty 변경 시 모든 HitObject의 Transformation 재구성.
        /// LoadBeatmap 전체 재생성 없이 UpdateDifficulty만 호출 — 성능 최적화.
        /// </summary>
        public void UpdateDifficulty(DifficultyValues newDifficulty)
        {
            this.difficulty = newDifficulty;

            // GamefieldSpriteRatio 업데이트 — CS 기반
            const int GamefieldSpriteRes = 128;
            spriteManager.GamefieldSpriteRatio = newDifficulty.SpriteDisplaySize / GamefieldSpriteRes;

            // 콤보 넘버 위치 재계산용 스케일 비율 갱신
            float gsr = spriteManager.GamefieldSpriteRatio;
            float gfr = renderer.GameField.Ratio;

            // 각 HitObject의 Transformation 재구성 + 스케일 비율 갱신
            foreach (HitCircleOsu circle in hitCircles)
            {
                circle.SetScaleRatios(gsr, gfr);
                circle.UpdateDifficulty(newDifficulty);
            }
            foreach (SliderOsu slider in sliders)
            {
                slider.SetScaleRatios(gsr, gfr);
                slider.UpdateDifficulty(newDifficulty);
            }
            foreach (SpinnerOsu spinner in spinners)
                spinner.UpdateDifficulty(newDifficulty);
        }

        public void Update(int timeMs, List<OsuMemoryReader.HitObjectJudgement> judgements)
        {
            // GamefieldSpriteRatio 매 프레임 업데이트 — GameField 리사이즈 후 반영
            const int GamefieldSpriteRes = 128;
            spriteManager.GamefieldSpriteRatio = difficulty.SpriteDisplaySize / GamefieldSpriteRes;

            // Retry 감지 — 시간이 크게 역행하면 모든 HitObject 상태 리셋
            if (lastUpdateTime > 0 && timeMs < lastUpdateTime - 2000)
            {
                // Retry — 모든 IsSpriteAdded 리셋, 스프라이트 제거
                foreach (HitCircleOsu c in hitCircles)
                {
                    if (c.IsSpriteAdded) { c.RemoveFromSpriteManager(spriteManager); c.IsSpriteAdded = false; }
                }
                foreach (SliderOsu s in sliders)
                {
                    if (s.IsSpriteAdded) { s.RemoveFromSpriteManager(spriteManager); s.IsSpriteAdded = false; }
                    s.ResetTickConsumption(); // 새 시도 — 소비된 틱 복원
                }
                foreach (SpinnerOsu sp in spinners)
                {
                    if (sp.IsSpriteAdded) { sp.RemoveFromSpriteManager(spriteManager); sp.IsSpriteAdded = false; }
                }
                foreach (pAnimation fp in followPoints)
                {
                    if (fp.TagNumeric == 1) { spriteManager.Remove(fp); fp.TagNumeric = 0; }
                }
                spriteManager.Clear();
                addedCircles.Clear(); addedCirclesList.Clear();
                addedSliders.Clear(); addedSlidersList.Clear();
                addedSpinners.Clear(); addedSpinnersList.Clear();
                addedFollowPoints.Clear(); addedFollowPointsList.Clear();
            }
            lastUpdateTime = timeMs;

            // 시간 윈도우 기반 스프라이트 동적 추가/제거
            UpdateSpriteWindow(timeMs);

            // 시간 윈도우 — 이 범위 밖의 HitObject는 처리 스킵 (성능 최적화)
            int timeWindow = difficulty.PreEmpt + 500; // PreEmpt + 여유
            int minTime = timeMs - timeWindow;
            int maxTime = timeMs + timeWindow;

            if (sortedCircles == null) BuildSortedLists();

            // 슬라이더 바디 먼저 렌더링 (depth buffer 사용, 스프라이트보다 아래)
            // 전체 순회 — 긴 슬라이더(34초+)는 binary search가 StartTime 기준이라 놓침
            if (sliderRenderer != null && renderer != null && renderer.GameField != null)
            {
                // 색상 할당 — 캐싱 (매 프레임 new List<Color> + GetComboColours() 호출 제거)
                float defaultRadius = difficulty.HitObjectRadius * renderer.GameField.Ratio;
                if (!sliderColoursValid || cachedSliderRadius != defaultRadius)
                {
                    cachedSliderColours = new List<Color>();
                    Color trackOverride = SkinManager.LoadColour("SliderTrackOverride");
                    if (trackOverride.A > 0)
                    {
                        cachedSliderColours.Add(trackOverride);
                    }
                    else
                    {
                        cachedSliderColours.AddRange(SkinManager.GetComboColours());
                    }
                    cachedSliderBorder = SkinManager.LoadColour("SliderBorder");
                    cachedSliderRadius = defaultRadius;
                    sliderColoursValid = true;
                }
                sliderRenderer.AssignColours(cachedSliderColours, cachedSliderBorder, cachedSliderRadius);

                // projection matrix — 창 크기 기준 (화면 좌표)
                Matrix4 projMatrix = Matrix4.CreateOrthographicOffCenter(0, renderer.ViewportWidth,
                    renderer.ViewportHeight, 0, -1, 1);

                for (int i = 0; i < sortedSliders.Count; i++)
                {
                    SliderOsu slider = sortedSliders[i];
                    if (slider.VirtualEndTime < minTime)
                        continue;

                    if (slider.IsVisibleAt(timeMs))
                    {
                        slider.DrawBody(sliderRenderer, renderer.GameField, timeMs, projMatrix, spriteManager);
                    }
                }
            }

            // HitCircle 판정 트리거 — binary search로 윈도우 내만 순회
            int hcStart = LowerBound(sortedCircles, c => c.Data.StartTime, minTime);
            int hcEnd = UpperBound(sortedCircles, c => c.Data.StartTime, maxTime);
            for (int i = hcStart; i < hcEnd; i++)
            {
                HitCircleOsu circle = sortedCircles[i];

                if (circle.IsArmed)
                    continue;

                if (judgements != null)
                {
                    foreach (var j in judgements)
                    {
                        if (j.StartTime == circle.Data.StartTime && (j.Type & 1) != 0)
                        {
                            if (j.IsHit == 1)
                            {
                                bool isHit = j.ScoreValue > 0;
                                circle.Arm(isHit, timeMs);
                            }
                            break;
                        }
                    }
                }

                // osu-stable HitObjectManager.UpdateHitObject:
                // EndTime + HitWindow50 < Time && !IsHit → Hit() → Arm(false)
                // 메모리 판정이 늦어도 AC/미스 페이드가 stable 타이밍에 맞게 들어가게 한다.
                if (!circle.IsArmed && timeMs > circle.Data.StartTime + difficulty.HitWindow50)
                    circle.Arm(false, timeMs);
            }

            // Slider tracking 상태 전달 + 판정 Arm — 전체 순회 (긴 슬라이더 지원)
            for (int i = 0; i < sortedSliders.Count; i++)
            {
                SliderOsu slider = sortedSliders[i];
                if (slider.VirtualEndTime < minTime)
                    continue;

                byte tracking = 0;
                byte startIsHit = 0;
                int startHitValue = 0;
                int startScoreValue = 0;
                if (judgements != null)
                {
                    foreach (var j in judgements)
                    {
                        if (j.StartTime == slider.Data.StartTime && (j.Type & 2) != 0)
                        {
                            tracking = j.IsTracking;
                            startIsHit = j.StartIsHit;
                            startHitValue = j.StartHitValue;
                            startScoreValue = j.StartScoreValue;
                            break;
                        }
                    }
                }

                // StartIsHit는 판정 완료 여부만 (miss도 1).
                // osu-stable: Arm(HitValue > 0). HitValue는 Hit()에서 IsHit와 동기 set.
                // ScoreValue는 IncreaseScore 이후라 IsHit=1 & ScoreValue=0 프레임에
                // Arm(false) 하면 hit 애니메이션이 영구히 씹힌다.
                if (!slider.StartCircleArmed && startIsHit == 1)
                {
                    if (startHitValue > 0 || startScoreValue > 0)
                        slider.ArmStartCircle(true, timeMs);
                    else if (startHitValue < 0) // IncreaseScoreType.Miss = -131072
                        slider.ArmStartCircle(false, timeMs);
                    // HitValue==0 && ScoreValue==0: 값 미기록 — timeout까지 대기
                }

                // osu-stable: StartTime+HitWindow50 < Time && !StartIsHit → Hit(start) → Arm(false)
                if (!slider.StartCircleArmed && timeMs > slider.Data.StartTime + difficulty.HitWindow50)
                    slider.ArmStartCircle(false, timeMs);

                slider.SetTracking(tracking);
                slider.UpdateSprites(timeMs);
            }

            // Spinner 회전 업데이트 — 전체 순회 (스피너는 맵당 몇 개 안 됨)
            for (int i = 0; i < sortedSpinners.Count; i++)
            {
                SpinnerOsu spinner = sortedSpinners[i];
                if (spinner.EndTime < minTime)
                    continue;

                float floatRot = 0;
                int spinState = 0;
                int scoringRot = 0;
                int memReq = 0;
                bool found = false;
                if (judgements != null)
                {
                    foreach (var j in judgements)
                    {
                        if (j.StartTime == spinner.StartTime && (j.Type & 8) != 0)
                        {
                            floatRot = j.FloatRotationCount;
                            spinState = j.SpinningState;
                            scoringRot = j.ScoringRotationCount;
                            memReq = j.RotationRequirement;
                            found = true;
                            break;
                        }
                    }
                }
                int memEndTime = 0;
                if (found)
                {
                    // osu-stable: 스피너는 클리어(Passed)해도 EndTime까지 시각적으로 유지됨.
                    // memEndTime를 현재 시간으로 설정하지 않음 — EndTime 그대로 사용.
                    // (spinState >= 2는 요구 회전수 달성을 의미, 스피너 종료가 아님)
                    if (floatRot > 0)
                        memEndTime = spinner.EndTime;
                }
                spinner.UpdateState(timeMs, floatRot, spinState, memEndTime, scoringRot, memReq);
            }
        }

        public void Dispose()
        {
            foreach (SliderOsu slider in sliders)
                slider.Dispose();
            sliders.Clear();

            if (sliderRenderer != null)
            {
                sliderRenderer.Dispose();
                sliderRenderer = null;
            }
        }
    }
}
