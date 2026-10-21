using System;
using System.Collections.Generic;
using ProjectHero.Core.Grid;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProjectHero.EditorTools
{
    internal sealed class ShapeCanvas : VisualElement
    {
        public Func<bool> BeginStroke;
        public Action<TrianglePoint, bool> Paint;
        public Action EndStroke;
        public Action<TrianglePoint?> Hover;
        public bool HasFitted { get; private set; }
        private readonly HashSet<TrianglePoint> selected = new HashSet<TrianglePoint>();
        private readonly HashSet<TrianglePoint> reference = new HashSet<TrianglePoint>();
        private float scale = 42;
        private Vector2 pan;
        private int direction;
        private bool unit;
        private int pointer = -1;
        private bool panning;
        private bool erase;
        private Vector2 last;
        private TrianglePoint? hovered;
        private Vector2 Origin => contentRect.center + pan;

        public ShapeCanvas()
        {
            AddToClassList("shape-canvas");
            focusable = true;
            generateVisualContent += Draw;
            RegisterCallback<PointerDownEvent>(Down);
            RegisterCallback<PointerMoveEvent>(Move);
            RegisterCallback<PointerUpEvent>(Up);
            RegisterCallback<PointerCaptureOutEvent>(_ => Finish());
            RegisterCallback<PointerCancelEvent>(_ => Finish());
            RegisterCallback<PointerLeaveEvent>(_ => { hovered = null; MarkDirtyRepaint(); });
            RegisterCallback<WheelEvent>(e =>
            {
                Vector2 before = (e.localMousePosition - Origin) / scale;
                scale = Mathf.Clamp(scale * Mathf.Exp(-e.delta.y * .07f), 12, 160);
                pan = e.localMousePosition - contentRect.center - before * scale;
                MarkDirtyRepaint(); e.StopPropagation();
            });
        }

        public void SetShape(IEnumerable<TrianglePoint> points, int facing, bool isUnit)
        {
            selected.Clear(); foreach (var point in points) selected.Add(point);
            direction = facing; unit = isUnit; MarkDirtyRepaint();
        }

        public void SetReferenceVolume(IEnumerable<TrianglePoint> points)
        {
            reference.Clear(); foreach (var point in points) reference.Add(point);
            MarkDirtyRepaint();
        }

        public void Fit()
        {
            if (contentRect.width < 1 || contentRect.height < 1) return;
            Vector2 min = new Vector2(-2, -2), max = new Vector2(2, 2);
            foreach (var p in CombinedPoints())
            {
                min = Vector2.Min(min, new Vector2((p.X - 1f) * .5f, -(p.Y + 1f) * .8660254f));
                max = Vector2.Max(max, new Vector2((p.X + 1f) * .5f, -(p.Y - 1f) * .8660254f));
            }
            scale = Mathf.Clamp(Mathf.Min((contentRect.width - 64) / (max.x - min.x), (contentRect.height - 64) / (max.y - min.y)), 12, 80);
            pan = -(min + max) * .5f * scale;
            HasFitted = true; MarkDirtyRepaint();
        }

        private IEnumerable<TrianglePoint> CombinedPoints()
        {
            foreach (var point in selected) yield return point;
            foreach (var point in reference) yield return point;
        }

        private Vector2 Position(float x, float y) => Origin + new Vector2(x * .5f, -y * .8660254f) * scale;
        private void Triangle(Painter2D painter, TrianglePoint point)
        {
            painter.BeginPath();
            painter.MoveTo(Position(point.X - 1f, point.Y));
            painter.LineTo(Position(point.X + 1f, point.Y));
            painter.LineTo(Position(point.X, point.Y + (float)point.T));
            painter.ClosePath();
        }

        private TrianglePoint? Hit(Vector2 local)
        {
            Vector2 grid = local - Origin;
            float x = grid.x * 2 / scale, y = -grid.y / (.8660254f * scale);
            int bx = Mathf.FloorToInt(x), by = Mathf.FloorToInt(y);
            for (int xx = bx - 1; xx <= bx + 1; xx++)
            for (int yy = by - 1; yy <= by + 1; yy++)
            for (int t = -1; t <= 1; t += 2)
            {
                if (!ProjectHero.Logic.Grid.TrianglePoint.IsValid(xx, yy, t)) continue;
                float height = (y - yy) * t;
                if (height >= 0 && height <= 1 && Mathf.Abs(x - xx) <= 1 - height + .00001f)
                    return new TrianglePoint(xx, yy, t);
            }
            return null;
        }

        private void Down(PointerDownEvent e)
        {
            if (e.button > 2 || pointer != -1) return;
            panning = e.button == 2;
            if (!panning && !(BeginStroke?.Invoke() ?? false)) return;
            Focus(); pointer = e.pointerId; last = e.localPosition;
            erase = e.button == 1 || e.shiftKey;
            this.CapturePointer(pointer);
            if (!panning) Sample(last);
            e.StopPropagation();
        }

        private void Move(PointerMoveEvent e)
        {
            Vector2 here = e.localPosition;
            hovered = Hit(here); Hover?.Invoke(hovered);
            if (pointer == e.pointerId)
            {
                if (panning) pan += here - last;
                else
                {
                    int samples = Mathf.CeilToInt(Vector2.Distance(last, here) / (scale * .15f));
                    for (int i = 1; i <= samples; i++) Sample(Vector2.Lerp(last, here, i / (float)samples));
                }
                last = here; e.StopPropagation();
            }
            MarkDirtyRepaint();
        }

        private void Sample(Vector2 local)
        {
            if (!contentRect.Contains(local)) return;
            var point = Hit(local); if (point.HasValue) Paint?.Invoke(point.Value, erase);
        }

        private void Up(PointerUpEvent e)
        {
            if (e.pointerId != pointer) return;
            int captured = pointer; Finish(); this.ReleasePointer(captured); e.StopPropagation();
        }

        private void Finish()
        {
            if (pointer == -1) return;
            if (!panning) EndStroke?.Invoke();
            pointer = -1;
        }

        private void Draw(MeshGenerationContext context)
        {
            if (contentRect.width < 1 || contentRect.height < 1) return;
            var p = context.painter2D;
            var o = Origin;
            int xmin = Mathf.FloorToInt(-o.x * 2 / scale) - 2;
            int xmax = Mathf.CeilToInt((contentRect.width - o.x) * 2 / scale) + 2;
            int ymin = Mathf.FloorToInt((o.y - contentRect.height) / (.8660254f * scale)) - 1;
            int ymax = Mathf.CeilToInt(o.y / (.8660254f * scale)) + 1;
            var fill = unit ? new Color(.2f, .72f, .86f, .78f) : new Color(.95f, .48f, .25f, .8f);
            for (int x = xmin; x <= xmax; x++)
            for (int y = ymin; y <= ymax; y++)
            for (int t = -1; t <= 1; t += 2)
            {
                if (!ProjectHero.Logic.Grid.TrianglePoint.IsValid(x, y, t)) continue;
                var point = new TrianglePoint(x, y, t);
                p.lineWidth = .7f; p.strokeColor = new Color(.36f, .46f, .56f, .45f);
                Triangle(p, point);
                if (reference.Contains(point)) { p.fillColor = new Color(.2f, .72f, .86f, .55f); p.Fill(); }
                if (selected.Contains(point)) { p.fillColor = fill; p.Fill(); }
                p.Stroke();
                if (reference.Contains(point))
                {
                    p.lineWidth = 2; p.strokeColor = new Color(.3f, .86f, 1f);
                    Triangle(p, point); p.Stroke();
                }
                if (hovered.HasValue && hovered.Value == point)
                {
                    p.lineWidth = 2; p.strokeColor = Color.white; Triangle(p, point); p.Stroke();
                }
            }
            p.lineWidth = 2; p.strokeColor = new Color(1, .83f, .34f);
            p.BeginPath(); p.MoveTo(o - Vector2.right * 7); p.LineTo(o + Vector2.right * 7);
            p.MoveTo(o - Vector2.up * 7); p.LineTo(o + Vector2.up * 7); p.Stroke();
            float angle = direction * 30 * Mathf.Deg2Rad;
            var forward = new Vector2(Mathf.Cos(angle), -Mathf.Sin(angle));
            var side = new Vector2(-forward.y, forward.x);
            var tip = o + forward * 48;
            p.BeginPath(); p.MoveTo(o + forward * 12); p.LineTo(tip);
            p.MoveTo(tip - forward * 9 + side * 5); p.LineTo(tip); p.LineTo(tip - forward * 9 - side * 5); p.Stroke();
        }
    }
}
