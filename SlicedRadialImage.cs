using UnityEngine;
using UnityEngine.Sprites;
using UnityEngine.UI;

public class SlicedRadialImage : Image
{
    const float EPS = 1e-6f;

    // 临时缓存，避免频繁分配
    static readonly Vector2[] s_VertScratch = new Vector2[4];
    static readonly Vector2[] s_UVScratch = new Vector2[4];
    static readonly Vector3[] s_Xy = new Vector3[4];
    static readonly Vector3[] s_Uv = new Vector3[4];

    // canonical 8 segments starting from bottom-center CCW
    static readonly int[] segX = { 1, 2, 2, 2, 1, 0, 0, 0 };
    static readonly int[] segY = { 0, 0, 1, 2, 2, 2, 1, 0 };
    static readonly int[] segCorner = { -1, 1, -1, 0, -1, 3, -1, 2 }; // -1 => center, else corner index

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        if (sprite == null)
        {
            base.OnPopulateMesh(vh);
            return;
        }

        GenerateSlicedSprite(vh);
    }

    private void GenerateSlicedSprite(VertexHelper vh)
    {
        vh.Clear();
        if (fillAmount <= 0f) return;

        var active = sprite;

        Vector4 outer = DataUtility.GetOuterUV(active);
        Vector4 inner = DataUtility.GetInnerUV(active);
        Vector4 padding = DataUtility.GetPadding(active);
        Vector4 border = active.border;

        Rect rect = GetPixelAdjustedRect();
        Vector4 adjustedBorders = GetAdjustedBorders(border / multipliedPixelsPerUnit, rect);
        padding /= multipliedPixelsPerUnit;

        // local quad coords (left-bottom origin)
        s_VertScratch[0] = new Vector2(padding.x, padding.y);
        s_VertScratch[1] = new Vector2(adjustedBorders.x, adjustedBorders.y);
        s_VertScratch[2] = new Vector2(rect.width - adjustedBorders.z, rect.height - adjustedBorders.w);
        s_VertScratch[3] = new Vector2(rect.width - padding.z, rect.height - padding.w);

        Vector2 adjustedSize = new Vector2(
            Mathf.Max(0f, s_VertScratch[2].x - s_VertScratch[1].x),
            Mathf.Max(0f, s_VertScratch[2].y - s_VertScratch[1].y)
        );

        // translate to rect position
        for (int i = 0; i < 4; ++i) s_VertScratch[i] += new Vector2(rect.x, rect.y);

        s_UVScratch[0] = new Vector2(outer.x, outer.y);
        s_UVScratch[1] = new Vector2(inner.x, inner.y);
        s_UVScratch[2] = new Vector2(inner.z, inner.w);
        s_UVScratch[3] = new Vector2(outer.z, outer.w);

        // corner radius approx (using left border)
        float cornerRadius = adjustedBorders.x;
        float cornerArcLength = (Mathf.PI * cornerRadius) / 2f;
        float perimeter = (adjustedSize.x + adjustedSize.y + (Mathf.PI * cornerRadius)) * 2f;
        float fillLen = Mathf.Clamp01(fillAmount) * perimeter;

        int startIndex = (fillOrigin % 4) * 2;
        int step = fillClockwise ? -1 : 1;

        // center directions for center slots: 0:bottom,1:right,2:top,3:left
        Vector2[] centerDirs = { Vector2.right, Vector2.up, Vector2.left, Vector2.down };

        // precompute side endpoints and UV endpoints for each side (bottom,right,top,left)
        Vector2[] sideA = new Vector2[4], sideB = new Vector2[4], sideMid = new Vector2[4];
        Vector2[] uvA = new Vector2[4], uvB = new Vector2[4];
        bool[] sideHorizontal = new bool[4];
        BuildSideInfo(sideA, sideB, sideMid, uvA, uvB, sideHorizontal);

        // iterate canonical 8 segments
        float lenAccum = 0f;
        int idx = startIndex;
        bool firstSegment = true;

        for (int s = 0; s < 8; ++s)
        {
            int x = segX[idx], y = segY[idx];
            int x2 = x + 1, y2 = y + 1;
            int corner = segCorner[idx];
            bool isCenter = (idx % 2 == 0);

            float segLen = isCenter ? ((y == 0 || y == 2) ? adjustedSize.x : adjustedSize.y) : cornerArcLength;
            if (isCenter && firstSegment) segLen *= 0.5f;

            if (fillLen >= lenAccum + segLen - EPS)
            {
                // fully filled segment
                Vector2 posMin = new Vector2(s_VertScratch[x].x, s_VertScratch[y].y);
                Vector2 posMax = new Vector2(s_VertScratch[x2].x, s_VertScratch[y2].y);

                if (isCenter && firstSegment && fillLen < perimeter - EPS)
                {
                    ApplyFirstSegmentCenterClip(ref posMin, ref posMax, idx, centerDirs, sideA, sideB, sideMid, sideHorizontal, step);
                }

                Vector2 uvMin = new Vector2(s_UVScratch[x].x, s_UVScratch[y].y);
                Vector2 uvMax = new Vector2(s_UVScratch[x2].x, s_UVScratch[y2].y);

                AddQuad(vh, posMin, posMax, color, uvMin, uvMax);
            }
            else if (fillLen > lenAccum + EPS)
            {
                // partially filled segment
                float p = fillLen - lenAccum;
                if (isCenter)
                    ProcessPartialCenter(vh, p, segLen, idx, firstSegment, centerDirs, sideA, sideB, sideMid, uvA, uvB, sideHorizontal, step);
                else
                    ProcessPartialCorner(vh, p, segLen, x, y, x2, y2, corner);
            }

            lenAccum += segLen;
            idx = (idx + step) & 7;
            firstSegment = false;
        }

        // handle trailing half-segment case (mirrors original logic)
        if (fillLen > (lenAccum + EPS) && fillLen < (perimeter - EPS))
        {
            float p = fillLen - lenAccum;
            int x = segX[idx], y = segY[idx];
            int x2 = x + 1, y2 = y + 1;
            bool isCenter = (idx % 2 == 0);
            float segLen = isCenter ? ((y == 0 || y == 2) ? adjustedSize.x : adjustedSize.y) : cornerArcLength;

            segLen *= 0.5f;
            float clamped = Mathf.Min(p, segLen);

            int centerSlot = (idx / 2) % 4;
            Vector2 dir = centerDirs[centerSlot] * step;
            if (sideHorizontal[centerSlot])
            {
                float leftX = Mathf.Min(sideA[centerSlot].x, sideB[centerSlot].x);
                float rightX = Mathf.Max(sideA[centerSlot].x, sideB[centerSlot].x);

                float xMin, xMax;
                float endX = leftX + dir.x * clamped;
                if (dir.x >= 0f)
                {
                    xMin = leftX;
                    xMax = Mathf.Clamp(Mathf.Max(leftX, endX), leftX, rightX);
                }
                else
                {
                    xMax = rightX;
                    xMin = Mathf.Clamp(rightX - clamped, leftX, rightX);
                }
                if (xMax > xMin + EPS)
                {
                    float uA = uvA[centerSlot].x, uB = uvB[centerSlot].x;
                    float tMin = (xMin - leftX) / Mathf.Max(EPS, rightX - leftX);
                    float tMax = (xMax - leftX) / Mathf.Max(EPS, rightX - leftX);
                    float uMin = Mathf.Lerp(uA, uB, tMin);
                    float uMax = Mathf.Lerp(uA, uB, tMax);

                    var posMin = new Vector2(xMin, Mathf.Min(s_VertScratch[y].y, s_VertScratch[y2].y));
                    var posMax = new Vector2(xMax, Mathf.Max(s_VertScratch[y].y, s_VertScratch[y2].y));
                    var uvMin = new Vector2(uMin, s_UVScratch[y].y);
                    var uvMax = new Vector2(uMax, s_UVScratch[y2].y);

                    AddQuad(vh, posMin, posMax, color, uvMin, uvMax);
                }
            }
            else
            {
                float bottomY = Mathf.Min(sideA[centerSlot].y, sideB[centerSlot].y);
                float topY = Mathf.Max(sideA[centerSlot].y, sideB[centerSlot].y);

                float yMin, yMax;
                float endY = bottomY + dir.y * clamped;

                if (dir.y >= 0f)
                {
                    yMin = bottomY;
                    yMax = Mathf.Clamp(Mathf.Max(bottomY, endY), bottomY, topY);
                }
                else
                {
                    yMax = topY;
                    yMin = Mathf.Clamp(topY - clamped, bottomY, topY);
                }

                if (yMax > yMin + EPS)
                {
                    float vA = uvA[centerSlot].y, vB = uvB[centerSlot].y;
                    float tMin = (yMin - bottomY) / Mathf.Max(EPS, topY - bottomY);
                    float tMax = (yMax - bottomY) / Mathf.Max(EPS, topY - bottomY);
                    float vMin = Mathf.Lerp(vA, vB, tMin);
                    float vMax = Mathf.Lerp(vA, vB, tMax);

                    var posMin = new Vector2(Mathf.Min(s_VertScratch[x].x, s_VertScratch[x2].x), yMin);
                    var posMax = new Vector2(Mathf.Max(s_VertScratch[x].x, s_VertScratch[x2].x), yMax);
                    var uvMin = new Vector2(s_UVScratch[x].x, vMin);
                    var uvMax = new Vector2(s_UVScratch[x2].x, vMax);

                    AddQuad(vh, posMin, posMax, color, uvMin, uvMax);
                }
            }
        }
    }

    #region Helpers & Smaller Operations

    private void BuildSideInfo(Vector2[] sideA, Vector2[] sideB, Vector2[] sideMid, Vector2[] uvA, Vector2[] uvB, bool[] sideHorizontal)
    {
        // bottom: left->right
        sideA[0] = new Vector2(s_VertScratch[1].x, s_VertScratch[0].y);
        sideB[0] = new Vector2(s_VertScratch[2].x, s_VertScratch[0].y);
        uvA[0] = new Vector2(s_UVScratch[1].x, s_UVScratch[0].y);
        uvB[0] = new Vector2(s_UVScratch[2].x, s_UVScratch[0].y);
        sideMid[0] = (sideA[0] + sideB[0]) * 0.5f;
        sideHorizontal[0] = true;

        // right: bottom->top
        sideA[1] = new Vector2(s_VertScratch[3].x, s_VertScratch[1].y);
        sideB[1] = new Vector2(s_VertScratch[3].x, s_VertScratch[2].y);
        uvA[1] = new Vector2(s_UVScratch[3].x, s_UVScratch[1].y);
        uvB[1] = new Vector2(s_UVScratch[3].x, s_UVScratch[2].y);
        sideMid[1] = (sideA[1] + sideB[1]) * 0.5f;
        sideHorizontal[1] = false;

        // top: left->right
        sideA[2] = new Vector2(s_VertScratch[1].x, s_VertScratch[3].y);
        sideB[2] = new Vector2(s_VertScratch[2].x, s_VertScratch[3].y);
        uvA[2] = new Vector2(s_UVScratch[1].x, s_UVScratch[3].y);
        uvB[2] = new Vector2(s_UVScratch[2].x, s_UVScratch[3].y);
        sideMid[2] = (sideA[2] + sideB[2]) * 0.5f;
        sideHorizontal[2] = true;

        // left: bottom->top
        sideA[3] = new Vector2(s_VertScratch[0].x, s_VertScratch[1].y);
        sideB[3] = new Vector2(s_VertScratch[0].x, s_VertScratch[2].y);
        uvA[3] = new Vector2(s_UVScratch[0].x, s_UVScratch[1].y);
        uvB[3] = new Vector2(s_UVScratch[0].x, s_UVScratch[2].y);
        sideMid[3] = (sideA[3] + sideB[3]) * 0.5f;
        sideHorizontal[3] = false;
    }

    private void ApplyFirstSegmentCenterClip(
        ref Vector2 posMin, ref Vector2 posMax, int idx,
        Vector2[] centerDirs, Vector2[] sideA, Vector2[] sideB, Vector2[] sideMid, bool[] sideHorizontal, int step)
    {
        int centerSlot = (idx / 2) % 4;
        Vector2 dir = centerDirs[centerSlot] * step;

        // use segX/segY to get the correct row/column indices for this segment
        int x = segX[idx];
        int y = segY[idx];
        int x2 = x + 1;
        int y2 = y + 1;

        if (sideHorizontal[centerSlot])
        {
            float midX = sideMid[centerSlot].x;
            float leftX = Mathf.Min(sideA[centerSlot].x, sideB[centerSlot].x);
            float rightX = Mathf.Max(sideA[centerSlot].x, sideB[centerSlot].x);

            float x0 = (dir.x >= 0f) ? midX : leftX;
            float x1 = (dir.x >= 0f) ? rightX : midX;
            posMin.x = x0;
            posMax.x = x1;

            // vertical extent for this band comes from the segment's y..y2
            posMin.y = Mathf.Min(s_VertScratch[y].y, s_VertScratch[y2].y);
            posMax.y = Mathf.Max(s_VertScratch[y].y, s_VertScratch[y2].y);
        }
        else
        {
            float midY = sideMid[centerSlot].y;
            float bottomY = Mathf.Min(sideA[centerSlot].y, sideB[centerSlot].y);
            float topY = Mathf.Max(sideA[centerSlot].y, sideB[centerSlot].y);

            float y0 = (dir.y >= 0f) ? midY : bottomY;
            float y1 = (dir.y >= 0f) ? topY : midY;
            posMin.y = y0;
            posMax.y = y1;

            // horizontal extent for this band comes from the segment's x..x2
            posMin.x = Mathf.Min(s_VertScratch[x].x, s_VertScratch[x2].x);
            posMax.x = Mathf.Max(s_VertScratch[x].x, s_VertScratch[x2].x);
        }
    }
    private void ProcessPartialCenter(
        VertexHelper vh, float p, float segLen, int idx, bool firstSegment,
        Vector2[] centerDirs, Vector2[] sideA, Vector2[] sideB, Vector2[] sideMid,
        Vector2[] uvA, Vector2[] uvB, bool[] sideHorizontal, int step)
    {
        int centerSlot = (idx / 2) % 4;
        Vector2 dir = centerDirs[centerSlot] * step;
        float pClamped = Mathf.Min(p, segLen);

        if (sideHorizontal[centerSlot])
        {
            float leftX = Mathf.Min(sideA[centerSlot].x, sideB[centerSlot].x);
            float rightX = Mathf.Max(sideA[centerSlot].x, sideB[centerSlot].x);
            float midX = sideMid[centerSlot].x;

            float xMin, xMax;
            if (firstSegment)
            {
                float endX = midX + dir.x * pClamped;
                xMin = Mathf.Clamp(Mathf.Min(midX, endX), leftX, rightX);
                xMax = Mathf.Clamp(Mathf.Max(midX, endX), leftX, rightX);
            }
            else
            {
                if (dir.x >= 0f)
                {
                    xMin = leftX;
                    xMax = Mathf.Clamp(leftX + pClamped, leftX, rightX);
                }
                else
                {
                    xMax = rightX;
                    xMin = Mathf.Clamp(rightX - pClamped, leftX, rightX);
                }
            }

            if (xMax > xMin + EPS)
            {
                float uAVal = uvA[centerSlot].x, uBVal = uvB[centerSlot].x;
                float tMin = (xMin - leftX) / Mathf.Max(EPS, rightX - leftX);
                float tMax = (xMax - leftX) / Mathf.Max(EPS, rightX - leftX);
                float uMin = Mathf.Lerp(uAVal, uBVal, tMin);
                float uMax = Mathf.Lerp(uAVal, uBVal, tMax);

                var posMin = new Vector2(xMin, Mathf.Min(s_VertScratch[idx % 2 == 0 ? idx / 2 : (idx / 2)].y, s_VertScratch[(idx / 2) + 1].y));
                var posMax = new Vector2(xMax, Mathf.Max(s_VertScratch[(idx / 2)].y, s_VertScratch[(idx / 2) + 1].y));
                var uvMin = new Vector2(uMin, s_UVScratch[segY[idx]].y);
                var uvMax = new Vector2(uMax, s_UVScratch[segY[idx] + 1].y);

                // fallback: simpler bounding for y-range (safe)
                posMin.y = Mathf.Min(s_VertScratch[segY[idx]].y, s_VertScratch[segY[idx] + 1].y);
                posMax.y = Mathf.Max(s_VertScratch[segY[idx]].y, s_VertScratch[segY[idx] + 1].y);

                AddQuad(vh, posMin, posMax, color, uvMin, uvMax);
            }
        }
        else
        {
            float bottomY = Mathf.Min(sideA[centerSlot].y, sideB[centerSlot].y);
            float topY = Mathf.Max(sideA[centerSlot].y, sideB[centerSlot].y);
            float midY = sideMid[centerSlot].y;

            float yMin, yMax;
            if (firstSegment)
            {
                float endY = midY + dir.y * pClamped;
                yMin = Mathf.Clamp(Mathf.Min(midY, endY), bottomY, topY);
                yMax = Mathf.Clamp(Mathf.Max(midY, endY), bottomY, topY);
            }
            else
            {
                if (dir.y >= 0f)
                {
                    yMin = bottomY;
                    yMax = Mathf.Clamp(bottomY + pClamped, bottomY, topY);
                }
                else
                {
                    yMax = topY;
                    yMin = Mathf.Clamp(topY - pClamped, bottomY, topY);
                }
            }

            if (yMax > yMin + EPS)
            {
                float vAVal = uvA[centerSlot].y, vBVal = uvB[centerSlot].y;
                float tMin = (yMin - bottomY) / Mathf.Max(EPS, topY - bottomY);
                float tMax = (yMax - bottomY) / Mathf.Max(EPS, topY - bottomY);
                float vMin = Mathf.Lerp(vAVal, vBVal, tMin);
                float vMax = Mathf.Lerp(vAVal, vBVal, tMax);

                var posMin = new Vector2(Mathf.Min(s_VertScratch[segX[idx]].x, s_VertScratch[segX[idx] + 1].x), yMin);
                var posMax = new Vector2(Mathf.Max(s_VertScratch[segX[idx]].x, s_VertScratch[segX[idx] + 1].x), yMax);
                var uvMin = new Vector2(s_UVScratch[segX[idx]].x, vMin);
                var uvMax = new Vector2(s_UVScratch[segX[idx] + 1].x, vMax);

                AddQuad(vh, posMin, posMax, color, uvMin, uvMax);
            }
        }
    }

    private void ProcessPartialCorner(VertexHelper vh, float p, float segLen, int x, int y, int x2, int y2, int corner)
    {
        if (segLen <= EPS) return;

        float angle = p / segLen;
        bool invert = fillClockwise;
        if ((corner & 1) == 1) invert = !invert;
        if (invert) angle = 1f - angle;

        float rad = angle * 90f * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);

        s_Xy[0] = new Vector2(s_VertScratch[x].x, s_VertScratch[y].y);
        s_Xy[1] = new Vector2(s_VertScratch[x].x, s_VertScratch[y2].y);
        s_Xy[2] = new Vector2(s_VertScratch[x2].x, s_VertScratch[y2].y);
        s_Xy[3] = new Vector2(s_VertScratch[x2].x, s_VertScratch[y].y);

        s_Uv[0] = new Vector2(s_UVScratch[x].x, s_UVScratch[y].y);
        s_Uv[1] = new Vector2(s_UVScratch[x].x, s_UVScratch[y2].y);
        s_Uv[2] = new Vector2(s_UVScratch[x2].x, s_UVScratch[y2].y);
        s_Uv[3] = new Vector2(s_UVScratch[x2].x, s_UVScratch[y].y);

        RadialCut(s_Xy, cos, sin, invert, corner);
        RadialCut(s_Uv, cos, sin, invert, corner);

        AddQuad(vh, s_Xy, color, s_Uv);
    }

    static void RadialCut(Vector3[] xy, float cos, float sin, bool invert, int corner)
    {
        int i0 = corner;
        int i1 = ((corner + 1) % 4);
        int i2 = ((corner + 2) % 4);
        int i3 = ((corner + 3) % 4);

        if ((corner & 1) == 1)
        {
            if (sin > cos)
            {
                cos /= sin; sin = 1f;
                if (invert) { xy[i1].x = Mathf.Lerp(xy[i0].x, xy[i2].x, cos); xy[i2].x = xy[i1].x; }
            }
            else if (cos > sin)
            {
                sin /= cos; cos = 1f;
                if (!invert) { xy[i2].y = Mathf.Lerp(xy[i0].y, xy[i2].y, sin); xy[i3].y = xy[i2].y; }
            }
            else { cos = 1f; sin = 1f; }

            if (!invert) xy[i3].x = Mathf.Lerp(xy[i0].x, xy[i2].x, cos);
            else xy[i1].y = Mathf.Lerp(xy[i0].y, xy[i2].y, sin);
        }
        else
        {
            if (cos > sin)
            {
                sin /= cos; cos = 1f;
                if (!invert) { xy[i1].y = Mathf.Lerp(xy[i0].y, xy[i2].y, sin); xy[i2].y = xy[i1].y; }
            }
            else if (sin > cos)
            {
                cos /= sin; sin = 1f;
                if (invert) { xy[i2].x = Mathf.Lerp(xy[i0].x, xy[i2].x, cos); xy[i3].x = xy[i2].x; }
            }
            else { cos = 1f; sin = 1f; }

            if (invert) xy[i3].y = Mathf.Lerp(xy[i0].y, xy[i2].y, sin);
            else xy[i1].x = Mathf.Lerp(xy[i0].x, xy[i2].x, cos);
        }
    }

    static void AddQuad(VertexHelper vertexHelper, Vector3[] quadPositions, Color32 color, Vector3[] quadUVs)
    {
        int startIndex = vertexHelper.currentVertCount;
        for (int i = 0; i < 4; ++i) vertexHelper.AddVert(quadPositions[i], color, quadUVs[i]);
        vertexHelper.AddTriangle(startIndex, startIndex + 1, startIndex + 2);
        vertexHelper.AddTriangle(startIndex + 2, startIndex + 3, startIndex);
    }

    static void AddQuad(VertexHelper vertexHelper, Vector2 posMin, Vector2 posMax, Color32 color, Vector2 uvMin, Vector2 uvMax)
    {
        int startIndex = vertexHelper.currentVertCount;
        vertexHelper.AddVert(new Vector3(posMin.x, posMin.y, 0), color, new Vector2(uvMin.x, uvMin.y));
        vertexHelper.AddVert(new Vector3(posMin.x, posMax.y, 0), color, new Vector2(uvMin.x, uvMax.y));
        vertexHelper.AddVert(new Vector3(posMax.x, posMax.y, 0), color, new Vector2(uvMax.x, uvMax.y));
        vertexHelper.AddVert(new Vector3(posMax.x, posMin.y, 0), color, new Vector2(uvMax.x, uvMin.y));
        vertexHelper.AddTriangle(startIndex, startIndex + 1, startIndex + 2);
        vertexHelper.AddTriangle(startIndex + 2, startIndex + 3, startIndex);
    }

    // From original, 保持行为一致
    private Vector4 GetAdjustedBorders(Vector4 border, Rect adjustedRect)
    {
        Rect originalRect = rectTransform.rect;

        for (int axis = 0; axis <= 1; axis++)
        {
            float borderScaleRatio;

            if (originalRect.size[axis] != 0)
            {
                borderScaleRatio = adjustedRect.size[axis] / originalRect.size[axis];
                border[axis] *= borderScaleRatio;
                border[axis + 2] *= borderScaleRatio;
            }

            float combinedBorders = border[axis] + border[axis + 2];
            if (adjustedRect.size[axis] < combinedBorders && combinedBorders != 0)
            {
                borderScaleRatio = adjustedRect.size[axis] / combinedBorders;
                border[axis] *= borderScaleRatio;
                border[axis + 2] *= borderScaleRatio;
            }
        }
        return border;
    }

    #endregion
}