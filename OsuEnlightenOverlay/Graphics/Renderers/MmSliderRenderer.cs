using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OsuEnlightenOverlay.Graphics.Batches;
using OsuEnlightenOverlay.Graphics.OpenGl;
using OsuEnlightenOverlay.Graphics.Primitives;
using OsuEnlightenOverlay.Helpers;
using OsuEnlightenOverlay.Gameplay.Difficulty;
using OsuEnlightenOverlay.Rendering;
using OsuEnlightenOverlay.Rendering.Sprites;
using OsuEnlightenOverlay.Rendering.Textures;

namespace OsuEnlightenOverlay.Graphics.Renderers
{
    /// <summary>
    /// 슬라이더 바디 렌더러 — osu! stable MmSliderRendererGL + TexturedSliderRendererGL 정확 포팅.
    /// 128x1 그라디언트 텍스처 + 쿼드 스트립 + 반원 캡 + depth buffer 자기 겹침.
    /// </summary>
    internal class MmSliderRenderer : IDisposable
    {
        const int MAXRES = 24; // 반원 캡 해상도
        const float QUAD_OVERLAP_FUDGE = 3.0e-4f;
        const float QUAD_MIDDLECRACK_FUDGE = 1.0e-4f;
        const float WEDGE_COUNT_FUDGE = 0.0f;
        const int TEX_WIDTH = 128;

        // 캡 메시 (반원 정점)
        Vector3[] capVertices;

        // 색상별 텍스처
        Dictionary<Color, int> textureCache = new Dictionary<Color, int>();

        // 배치
        QuadBatch3D quadBatch;
        LinearBatch3D halfCircleBatch;

        // 캡 메시 인덱스
        int numPrimitives_cap;

        // 렌더링 상태
        List<Color> m_colours = new List<Color>();
        Color m_border = Color.White;
        float m_default_radius = 64.0f;

        Shader textureShader3D;
        Shader colourShader2D;
        Shader textureShader2D;

        public MmSliderRenderer(ShaderManager shaderManager)
        {
            textureShader3D = shaderManager.TextureShader3D;
            colourShader2D = shaderManager.ColourShader2D;
            textureShader2D = shaderManager.TextureShader2D;

            quadBatch = new QuadBatch3D(200 * 6);
            halfCircleBatch = new LinearBatch3D(MAXRES * 100 * 3);

            CalculateCapMesh();
        }

        /// <summary>
        /// 반원 캡 메시 계산 — osu! stable CalculateCapMesh.
        /// </summary>
        void CalculateCapMesh()
        {
            capVertices = new Vector3[MAXRES + 1];

            float maxRes = (float)MAXRES;
            float step = (float)Math.PI / maxRes;

            capVertices[0] = new Vector3(0.0f, -1.0f, 0.0f);

            for (int z = 1; z < MAXRES; z++)
            {
                float angle = (float)z * step;
                capVertices[z] = new Vector3((float)Math.Sin(angle), -(float)Math.Cos(angle), 0.0f);
            }

            capVertices[MAXRES] = new Vector3(0.0f, 1.0f, 0.0f);

            numPrimitives_cap = MAXRES;
        }

        /// <summary>
        /// 색상 할당 — osu! stable AssignColours.
        /// </summary>
        public void AssignColours(List<Color> colours, Color border, float defaultRadius)
        {
            // 참조 저장 — 매 프레임 new List<Color>(colours) 복사 제거
            // 호출자(HitObjectManagerOsu)가 캐싱된 리스트를 재사용하므로 안전
            m_colours = colours;
            m_border = border;
            m_default_radius = defaultRadius;
        }

        /// <summary>
        /// 슬라이더 색상 → Inner/Outer 계산 — osu! stable ComputeSliderColour.
        /// </summary>
        void ComputeSliderColour(Color colour, out Color innerColour, out Color outerColour)
        {
            Color col = Color.FromArgb(180, colour.R, colour.G, colour.B);
            innerColour = ColourHelper.Lighten2(col, 0.5f);
            outerColour = ColourHelper.Darken(col, 0.1f);
        }

        /// <summary>
        /// 128x1 그라디언트 텍스처 생성 — osu! stable RenderSliderTexture.
        /// FBO에 LineStrip으로 그라디언트 렌더링.
        /// </summary>
        int CreateTexture(Color colour, Color border)
        {
            Color inner, outer;
            ComputeSliderColour(colour, out inner, out outer);

            // aa_width — osu! stable 공식
            float aa_width = Math.Min(Math.Max(0.5f / m_default_radius, 3.0f / 256.0f), 1.0f / 16.0f);
            Color shadow = Color.FromArgb(64, 0, 0, 0);

            return RenderSliderTexture(shadow, border, inner, outer, aa_width);
        }

        /// <summary>
        /// 128x1 그라디언트 텍스처 생성 — osu! stable MmSliderRendererGL.RenderSliderTexture 포팅.
        /// CPU에서 픽셀 직접 계산 (FBO 1px 라인 레스터라이제이션 문제 회피).
        /// </summary>
        int RenderSliderTexture(Color shadow, Color border, Color innerColour, Color outerColour, float aa_width)
        {
            int width = TEX_WIDTH;
            byte[] pixels = new byte[width * 4]; // RGBA

            for (int x = 0; x < width; x++)
            {
                float t = (float)x / (width - 1); // 0.0 ~ 1.0
                Color c;

                if (t < 0.078125f - aa_width)
                {
                    // 투명 영역
                    c = Color.Transparent;
                }
                else if (t < 0.078125f + aa_width)
                {
                    // shadow → border AA
                    float blend = (t - (0.078125f - aa_width)) / (2 * aa_width);
                    c = ColourHelper.ColourLerp(shadow, border, blend);
                }
                else if (t < 0.1875f - aa_width)
                {
                    // border
                    c = border;
                }
                else if (t < 0.1875f + aa_width)
                {
                    // border → outer AA
                    float blend = (t - (0.1875f - aa_width)) / (2 * aa_width);
                    c = ColourHelper.ColourLerp(border, outerColour, blend);
                }
                else
                {
                    // outer → inner 그라디언트
                    float blend = (t - (0.1875f + aa_width)) / (1.0f - (0.1875f + aa_width));
                    blend = Math.Max(0, Math.Min(1, blend));
                    c = ColourHelper.ColourLerp(outerColour, innerColour, blend);
                }

                pixels[x * 4 + 0] = c.R;
                pixels[x * 4 + 1] = c.G;
                pixels[x * 4 + 2] = c.B;
                pixels[x * 4 + 3] = c.A;
            }

            int texId;
            GL.GenTextures(1, out texId);
            GL.BindTexture(TextureTarget.Texture2D, texId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            return texId;
        }

        /// <summary>
        /// 쿼드 그리기 — osu! stable glDrawQuad.
        /// 각 쿼드는 2개 하위 쿼드로 분할, 중간 정점은 z=1 (depth buffer 자기 겹침).
        /// osu! stable은 QuadBatch(인덱스 버퍼)를 사용하지만,
        /// 오버레이는 DrawArrays(Triangles)를 사용하므로 명시적 삼각형 리스트로 변환.
        /// 
        /// osu! stable QuadBatch 인덱스: i, i+1, i+3, i+2, i+3, i+1
        ///   쿼드1 (v0,v1,v2,v3): 삼각형 (v0,v1,v3), (v2,v3,v1)
        ///   쿼드2 (v4,v5,v6,v7): 삼각형 (v4,v5,v7), (v6,v7,v5)
        /// 삼각형 리스트로 변환:
        ///   삼각형1: v0, v1, v3
        ///   삼각형2: v2, v3, v1
        ///   삼각형3: v4, v5, v7
        ///   삼각형4: v6, v7, v5
        /// </summary>
        void glDrawQuad(Matrix4 m)
        {
            Vector3 v0 = TransformVector3(new Vector3(-QUAD_OVERLAP_FUDGE, -1.0f, 0.0f), m);
            Vector3 v1 = TransformVector3(new Vector3(1.0f + QUAD_OVERLAP_FUDGE, -1.0f, 0.0f), m);
            Vector3 v2 = TransformVector3(new Vector3(1.0f + QUAD_OVERLAP_FUDGE, QUAD_MIDDLECRACK_FUDGE, 1.0f), m);
            Vector3 v3 = TransformVector3(new Vector3(-QUAD_OVERLAP_FUDGE, QUAD_MIDDLECRACK_FUDGE, 1.0f), m);
            Vector3 v6 = TransformVector3(new Vector3(-QUAD_OVERLAP_FUDGE, 1.0f, 0.0f), m);
            Vector3 v7 = TransformVector3(new Vector3(1.0f + QUAD_OVERLAP_FUDGE, 1.0f, 0.0f), m);

            Vector2 uv0 = new Vector2(0, 0);
            Vector2 uvMid = new Vector2(1.0f - 1.0f / TEX_WIDTH, 0);
            Color4b white = Color.White;

            // 삼각형1: v0, v1, v3 (osu! 인덱스: 0, 1, 3)
            quadBatch.Add(new TexturedVertex3d { Position = v0, TexturePosition = uv0, Colour = white });
            quadBatch.Add(new TexturedVertex3d { Position = v1, TexturePosition = uv0, Colour = white });
            quadBatch.Add(new TexturedVertex3d { Position = v3, TexturePosition = uvMid, Colour = white });
            // 삼각형2: v2, v3, v1 (osu! 인덱스: 2, 3, 1)
            quadBatch.Add(new TexturedVertex3d { Position = v2, TexturePosition = uvMid, Colour = white });
            quadBatch.Add(new TexturedVertex3d { Position = v3, TexturePosition = uvMid, Colour = white });
            quadBatch.Add(new TexturedVertex3d { Position = v1, TexturePosition = uv0, Colour = white });
            // 삼각형3: v4(=v2), v5(=v3), v7 (osu! 인덱스: 4, 5, 7)
            quadBatch.Add(new TexturedVertex3d { Position = v2, TexturePosition = uvMid, Colour = white });
            quadBatch.Add(new TexturedVertex3d { Position = v3, TexturePosition = uvMid, Colour = white });
            quadBatch.Add(new TexturedVertex3d { Position = v7, TexturePosition = uv0, Colour = white });
            // 삼각형4: v6, v7, v5(=v3) (osu! 인덱스: 6, 7, 5)
            quadBatch.Add(new TexturedVertex3d { Position = v6, TexturePosition = uv0, Colour = white });
            quadBatch.Add(new TexturedVertex3d { Position = v7, TexturePosition = uv0, Colour = white });
            quadBatch.Add(new TexturedVertex3d { Position = v3, TexturePosition = uvMid, Colour = white });
        }

        /// <summary>
        /// 반원 캡 그리기 — osu! stable glDrawHalfCircle.
        /// </summary>
        void glDrawHalfCircle(int count, Matrix4 m)
        {
            if (count == 0) return;
            if (count > MAXRES) count = MAXRES;

            Vector3 centerPoint = TransformVector3(new Vector3(0, 0, 1.0f), m);
            Vector3 currentPoint = TransformVector3(capVertices[0], m);

            for (int i = 0; i <= count - 1; i++)
            {
                halfCircleBatch.Add(new TexturedVertex3d
                {
                    Position = centerPoint,
                    TexturePosition = new Vector2(1.0f - 1.0f / TEX_WIDTH, 0),
                    Colour = Color.White
                });
                halfCircleBatch.Add(new TexturedVertex3d
                {
                    Position = currentPoint,
                    TexturePosition = new Vector2(0, 0),
                    Colour = Color.White
                });

                currentPoint = TransformVector3(capVertices[i + 1], m);

                halfCircleBatch.Add(new TexturedVertex3d
                {
                    Position = currentPoint,
                    TexturePosition = new Vector2(0, 0),
                    Colour = Color.White
                });
            }
        }

        /// <summary>
        /// Vector3 변환 — Matrix4 적용.
        /// OpenTK = XNA: v * M (row vector)
        /// result.X = v.X*M.M11 + v.Y*M.M21 + v.Z*M.M31 + M.M41
        /// result.Y = v.X*M.M12 + v.Y*M.M22 + v.Z*M.M32 + M.M42
        /// result.Z = v.X*M.M13 + v.Y*M.M23 + v.Z*M.M33 + M.M43
        /// </summary>
        Vector3 TransformVector3(Vector3 vec, Matrix4 m)
        {
            return new Vector3(
                vec.X * m.M11 + vec.Y * m.M21 + vec.Z * m.M31 + m.M41,
                vec.X * m.M12 + vec.Y * m.M22 + vec.Z * m.M32 + m.M42,
                vec.X * m.M13 + vec.Y * m.M23 + vec.Z * m.M33 + m.M43
            );
        }

        /// <summary>
        /// 핵심 드로우 — osu! stable SliderOsu.Draw() + DrawOGL 정확 포팅.
        /// FBO에 blend OFF로 렌더링 → pSprite로 반환 (SpriteManager가 합성).
        /// out fboTarget: 생성된 RenderTarget2D (호출자가 Dispose 책임).
        /// </summary>
        /// <summary>
        /// 바디를 FBO에 그린다.
        /// drawLeft/Top/Width/Height는 **전체 커브 기준 고정 영역**이라 스네이킹 중에도 변하지 않는다.
        /// 덕분에 fboTarget과 bodySprite를 재사용할 수 있다 — 예전에는 진행도 1/30마다 FBO를
        /// 새로 만들고 부숴서 VRAM 할당/해제가 초당 수백 번 일어났다 (D3).
        /// fboTarget/bodySprite가 null이면 만들고, 아니면 내용만 다시 그린다.
        /// </summary>
        public pSprite Draw(List<Line> lineList, float globalRadius, int colourIndex,
            Matrix4 projectionMatrix, int startTime, int endTime, int preEmpt, int fadeIn,
            float drawLeft, float drawTop, float drawWidth, float drawHeight,
            ref RenderTarget2D fboTarget, pSprite bodySprite)
        {
            if (lineList.Count == 0) return bodySprite;

            // 색상 인덱스 → 텍스처
            Color colour;
            if (colourIndex < 0 || colourIndex >= m_colours.Count)
                colour = Color.Gray;
            else
                colour = m_colours[colourIndex];

            // 그라디언트 텍스처 캐싱
            int texId;
            Color cacheKey = Color.FromArgb(colour.A, colour.R, colour.G, colour.B);
            if (!textureCache.TryGetValue(cacheKey, out texId))
            {
                texId = CreateTexture(colour, m_border);
                textureCache[cacheKey] = texId;
            }

            // bounding box는 호출자가 전체 커브 기준으로 계산해서 넘긴다 (D3).
            // 여기서 lineList로 계산하면 스네이킹마다 크기가 달라져 FBO를 재사용할 수 없다.

            // NPOT 텍스처 — desktop GL 지원, POT 불필요
            int fboWidth = Math.Max(1, (int)Math.Ceiling(drawWidth));
            int fboHeight = Math.Max(1, (int)Math.Ceiling(drawHeight));

            // FBO 생성 (depth buffer 포함) — osu! stable: new RenderTarget2D(texture, DepthComponent16)
            // 이미 있으면 재사용 — 크기가 고정이라 내용만 다시 그리면 된다.
            if (fboTarget == null)
                fboTarget = new RenderTarget2D(fboWidth, fboHeight, true, true);
            RenderTarget2D target = fboTarget;

            target.Bind();

            GL.ClearColor(0, 0, 0, 0);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // FBO용 projection — OpenGL 좌표계 (좌하단 원점, Y가 위로 증가)
            // drawTop이 화면에서 위쪽이므로, OpenGL에서는 bottom = drawTop, top = drawTop + drawHeight
            Matrix4 fboProj = Matrix4.CreateOrthographicOffCenter(
                drawLeft, drawLeft + drawWidth,
                drawTop, drawTop + drawHeight, -1, 1);

            // FBO에 슬라이더 바디 렌더링 — blend OFF, depth test ON
            DrawOGL(lineList, globalRadius, texId, fboProj);

            target.Unbind();

            // 스프라이트는 위치·크기·페이드가 전부 고정이라 한 번만 만들면 된다.
            // FBO 텍스처 id도 그대로이므로 내용만 갱신되면 화면에 반영된다 (D3).
            if (bodySprite != null)
                return bodySprite;

            // ── pSprite 생성 — osu! stable: sliderBody.Texture = FBO 텍스처 ──
            // pTexture 래퍼 생성 — Width/Height를 drawWidth/drawHeight로 설정
            // (POT 텍스처이지만 사용 영역만큼만 표시)
            pTexture pTex = new pTexture(target.TextureId, (int)drawWidth, (int)drawHeight, "sliderBody");
            pTex.IsDisposable = false; // RenderTarget2D가 소유

            // pSprite 생성 — Fields.Native, Origins.TopLeft
            // depth: DrawOrderBwd(EndTime + 10) — 시작원/끝원보다 낮아야 아래에 그려짐
            // DrawOrderBwd(n) = 0.8 - (n%6000000)/10000000 이므로
            // EndTime+10 > StartTime → DrawOrderBwd(EndTime+10) < DrawOrderBwd(StartTime) → 아래
            pSprite sliderBody = new pSprite(pTex, Fields.Native, Origins.TopLeft, Clocks.Audio,
                new Vector2(drawLeft, drawTop),
                SpriteManager.DrawOrderBwd(endTime + 10), false, Color.White);
            sliderBody.Alpha = 0f;
            // FBO 텍스처 — DpiScale 처리 방식 변경 후 FlipVertical 불필요
            // sliderBody.FlipVertical = true;

            // Fade transformations — osu! stable SliderOsu.cs:965-967
            // Fade In: 0→1 (StartTime-PreEmpt → StartTime-PreEmpt+FadeIn)
            // FadeIn을 PreEmpt로 클램프 — stable(AR≤10, PreEmpt≥450)에선 무의미(=stable 동일)하고,
            // 오버라이드 고AR(PreEmpt<400)에서만 페이드인이 슬라이더 시작을 넘지 않게 막는다.
            int fadeInClamped = Math.Min(fadeIn, preEmpt);
            sliderBody.Transformations.Add(new Transformation(
                TransformationType.Fade, 0f, 1f,
                startTime - preEmpt,
                startTime - preEmpt + fadeInClamped,
                EasingTypes.None));
            // Fade Out — HD: 1→0 (Start-PreEmpt+FadeIn → EndTime, EasingTypes.Out)
            //               nomod: 1→0 (EndTime → EndTime+FadeOut)
            if (OsuEnlightenOverlay.Gameplay.HitObjects.HitCircleOsu.HiddenActive)
            {
                sliderBody.Transformations.Add(new Transformation(
                    TransformationType.Fade, 1f, 0f,
                    startTime - preEmpt + fadeInClamped,
                    endTime,
                    EasingTypes.Out));
            }
            else
            {
                sliderBody.Transformations.Add(new Transformation(
                    TransformationType.Fade, 1f, 0f,
                    endTime,
                    endTime + DifficultyCalculator.FadeOut,
                    EasingTypes.None));
            }

            return sliderBody;
        }

        static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        void DrawOGL(List<Line> lineList, float globalRadius, int textureId, Matrix4 projectionMatrix)
        {
            // osu! stable: blend OFF, depth test ON — FBO에 렌더링
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthMask(true);
            GL.DepthFunc(DepthFunction.Lequal);

            GL.Clear(ClearBufferMask.DepthBufferBit);

            if (textureShader3D != null && textureShader3D.IsValid)
            {
                textureShader3D.Begin();
                textureShader3D.SetProjectionMatrix(ref projectionMatrix);
                textureShader3D.SetColour(Color.White);
            }

            GL.BindTexture(TextureTarget.Texture2D, textureId);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            quadBatch.Initialize();
            halfCircleBatch.Initialize();

            int count = lineList.Count;
            Line prev = null;

            for (int x = 1; x < count; x++)
            {
                DrawLineOGL(prev, lineList[x - 1], lineList[x], globalRadius);
                prev = lineList[x - 1];
            }

            DrawLineOGL(prev, lineList[count - 1], null, globalRadius);

            quadBatch.Draw(textureShader3D);
            halfCircleBatch.Draw(textureShader3D);

            if (textureShader3D != null && textureShader3D.IsValid)
                textureShader3D.End();

            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Enable(EnableCap.Blend);
        }

        /// <summary>
        /// 선분 드로우 — osu! stable DrawLineOGL.
        /// 쿼드 + 끝 캡 + 시작 캡.
        /// </summary>
        void DrawLineOGL(Line prev, Line curr, Line next, float globalRadius)
        {
            // 쿼드 매트릭스: Scale * WorldMatrix → scale → rotate → translate
            Matrix4 m = CreateSliderMatrix(curr.rho, globalRadius) * curr.WorldMatrix();
            glDrawQuad(m);

            // 끝 캡
            int end_triangles;
            bool flip;

            if (next == null)
            {
                flip = false;
                end_triangles = numPrimitives_cap;
            }
            else
            {
                float theta = next.theta - curr.theta;

                if (theta > Math.PI) theta -= (float)(Math.PI * 2);
                if (theta < -Math.PI) theta += (float)(Math.PI * 2);

                if (theta < 0)
                {
                    flip = true;
                    end_triangles = (int)Math.Ceiling((-theta) * MAXRES / Math.PI + WEDGE_COUNT_FUDGE);
                }
                else if (theta > 0)
                {
                    flip = false;
                    end_triangles = (int)Math.Ceiling(theta * MAXRES / Math.PI + WEDGE_COUNT_FUDGE);
                }
                else
                {
                    flip = false;
                    end_triangles = 0;
                }
            }
            end_triangles = Math.Min(end_triangles, numPrimitives_cap);

            if (flip)
                m = CreateSliderMatrix(globalRadius, -globalRadius) * curr.EndWorldMatrix();
            else
                m = CreateSliderMatrix(globalRadius, globalRadius) * curr.EndWorldMatrix();

            glDrawHalfCircle(end_triangles, m);

            // 시작 캡
            bool hasStartCap = false;
            if (prev == null) hasStartCap = true;
            else if (curr.p1 != prev.p2) hasStartCap = true;

            if (hasStartCap)
            {
                m = CreateSliderMatrix(-globalRadius, -globalRadius) * curr.WorldMatrix();
                glDrawHalfCircle(numPrimitives_cap, m);
            }
        }

        /// <summary>
        /// 슬라이더 매트릭스 생성 — osu! stable Matrix(row-major).
        /// XNA Matrix: M11=row1col1, OpenTK Matrix4: Row0=M11,M12,M13,M14.
        /// osu! stable: new Matrix(rho,0,0,0, 0,radius,0,0, 0,0,1,0, 0,0,0,1)
        /// = scale(rho, radius, 1)
        /// </summary>
        Matrix4 CreateSliderMatrix(float scaleX, float scaleY)
        {
            return Matrix4.CreateScale(scaleX, scaleY, 1);
        }

        public void Dispose()
        {
            foreach (var kv in textureCache)
            {
                int id = kv.Value;
                if (id > 0)
                    GL.DeleteTextures(1, ref id);
            }
            textureCache.Clear();

            if (quadBatch != null)
            {
                quadBatch.Dispose();
                quadBatch = null;
            }
            if (halfCircleBatch != null)
            {
                halfCircleBatch.Dispose();
                halfCircleBatch = null;
            }
        }
    }
}
