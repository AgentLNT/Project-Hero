using UnityEngine;
using UnityEngine.InputSystem;
using System;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Entities;

namespace ProjectHero.Core.Input
{
    public class InputManager : MonoBehaviour
    {
        public static InputManager Instance { get; private set; }

        public event Action<TrianglePoint> OnTileHover;
        public event Action<TrianglePoint> OnTileClick;
        public event Action<CombatUnit> OnUnitClick;

        [Header("Settings")]
        public LayerMask groundLayer;
        public LayerMask unitLayer;

        [Header("Input Actions")]
        public InputActionAsset inputActions; // Assign in Inspector
        private InputAction _pointAction;
        private InputAction _clickAction;

        private Camera _mainCamera;
        private TrianglePoint _lastHoveredTile;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);

            _mainCamera = Camera.main;
            SetupInput();
        }

        private void SetupInput()
        {
            if (inputActions == null)
            {
                // Fallback or create default actions via code if asset is missing
                // For simplicity, let's assume we create a simple map in code if not provided
                var map = new InputActionMap("Gameplay");
                // Important: Set type to Value for position to ensure correct reading
                _pointAction = map.AddAction("Point", type: InputActionType.Value, binding: "<Mouse>/position");
                _clickAction = map.AddAction("Click", type: InputActionType.Button, binding: "<Mouse>/leftButton");
                
                _pointAction.Enable();
                _clickAction.Enable();
            }
            else
            {
                // Assuming standard naming conventions
                var map = inputActions.FindActionMap("Gameplay"); // Or "Player"
                if (map != null)
                {
                    _pointAction = map.FindAction("Point");
                    _clickAction = map.FindAction("Click"); // Or "Fire"
                }
            }

            if (_clickAction != null)
            {
                _clickAction.performed += OnClickPerformed;
            }
        }

        private void OnEnable()
        {
            _pointAction?.Enable();
            _clickAction?.Enable();
        }

        private void OnDisable()
        {
            _pointAction?.Disable();
            _clickAction?.Disable();
        }

        private void Update()
        {
            HandleHover();
        }

        private void HandleHover()
        {
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null || _pointAction == null) return;

            Vector2 mousePos = _pointAction.ReadValue<Vector2>();
            Ray ray = _mainCamera.ScreenPointToRay(mousePos);

            if (UnityEngine.Physics.Raycast(ray, out RaycastHit groundHit, 100f, groundLayer))
            {
                TrianglePoint hoveredTile = WorldToTriangle(groundHit.point);
                
                if (!hoveredTile.Equals(_lastHoveredTile))
                {
                    _lastHoveredTile = hoveredTile;
                    OnTileHover?.Invoke(hoveredTile);
                }
            }
        }

        private void OnClickPerformed(InputAction.CallbackContext context)
        {
            if (_mainCamera == null) return;

            Vector2 mousePos = _pointAction.ReadValue<Vector2>();
            Ray ray = _mainCamera.ScreenPointToRay(mousePos);
            
            // Debug: Visualize the click ray
            Debug.DrawRay(ray.origin, ray.direction * 100f, Color.yellow, 1.0f);

            // Ensure layers are not Nothing (0) if they haven't been set
            if (unitLayer.value == 0) unitLayer = LayerMask.GetMask("Default");
            if (groundLayer.value == 0) groundLayer = LayerMask.GetMask("Default");

            // 1. Check for Unit Click
            if (UnityEngine.Physics.Raycast(ray, out RaycastHit unitHit, 100f, unitLayer))
            {
                CombatUnit unit = unitHit.collider.GetComponentInParent<CombatUnit>();
                if (unit != null)
                {
                    Debug.Log($"[Input] Clicked Unit: {unit.name}");
                    OnUnitClick?.Invoke(unit);
                    return; 
                }
            }

            // 2. Check for Ground Click
            if (UnityEngine.Physics.Raycast(ray, out RaycastHit groundHit, 100f, groundLayer))
            {
                TrianglePoint clickedTile = WorldToTriangle(groundHit.point);
                Debug.Log($"[Input] Clicked Tile: {clickedTile}");
                OnTileClick?.Invoke(clickedTile);
            }
        }

        // Helper to convert World Position to the specific Triangle (X, Y, T)
        private TrianglePoint WorldToTriangle(Vector3 worldPos)
        {
            if (GridManager.Instance == null) return new TrianglePoint();

            var gp = GridManager.Instance.WorldToGrid(worldPos);
            
            var t1 = new TrianglePoint(gp.X, gp.Y, 1);
            var t2 = new TrianglePoint(gp.X, gp.Y, -1);
            
            float d1 = Vector3.Distance(worldPos, GridManager.Instance.GetTriangleCenter(t1));
            float d2 = Vector3.Distance(worldPos, GridManager.Instance.GetTriangleCenter(t2));
            
            return d1 < d2 ? t1 : t2;
        }
    }
}
