#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems; // Added for UI check
using System;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Entities;

namespace ProjectHero.Core.Input
{
    public class InputManager : MonoBehaviour
    {
        public static InputManager Instance { get; private set; }

        // Changed: Now returns raw world position so Controller can decide (Triangle vs Vertex)
        public event Action<Vector3> OnGroundHover;
        public event Action<Vector3> OnGroundClick;
        public event Action<CombatUnit> OnUnitClick;
        public event Action OnCancel; // Right Click

        [Header("Settings")]
        public LayerMask groundLayer;
        public LayerMask unitLayer;

        [Header("Input Actions")]
        public InputActionAsset inputActions; // Assign in Inspector
        private InputAction _pointAction;
        private InputAction _clickAction;
        private InputAction _cancelAction;

        public Camera _mainCamera;
        private Vector3 _lastHoveredPoint;

        // ─────────────────────────────────────────────────────────────────────
        // 任务 09：输入模式状态机取代旧布尔通信。
        //
        // 旧 `IgnoreUnitClicks` 布尔量已删除：它曾经让"当前是否在选目标"这一类
        // **表现层**事实直接控制点击路由，从而与战斗逻辑混在一起。
        // 现在"单位点击是否生效 / 世界点击是否生效"由注入的
        // `ViewInputController` 的当前模式推导（纯视图判断），
        // 未注入时不吞任何点击（保持旧外部行为），并且**不再影响任何逻辑路径**。
        // ─────────────────────────────────────────────────────────────────────
        [Header("Task 09 输入模式（可空）")]
        [Tooltip("注入后：单位/世界点击是否生效由输入模式状态机决定；本组件只做转发。")]
        public ViewInputController InputController;

        /// <summary>由输入模式状态机推得的"当前是否吞掉单位点击"（纯视图路由）。</summary>
        public bool SuppressUnitClicks => InputController != null && InputController.SuppressesUnitClicks;

        /// <summary>由输入模式状态机推得的"当前是否吞掉世界点击"（纯视图路由）。</summary>
        public bool SuppressWorldClicks => InputController != null && InputController.SuppressesWorldClicks;

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
                var map = new InputActionMap("Gameplay");
                // Important: Set type to Value for position to ensure correct reading
                _pointAction = map.AddAction("Point", type: InputActionType.Value, binding: "<Mouse>/position");
                _clickAction = map.AddAction("Click", type: InputActionType.Button, binding: "<Mouse>/leftButton");
                _cancelAction = map.AddAction("Cancel", type: InputActionType.Button, binding: "<Mouse>/rightButton");
                
                _pointAction.Enable();
                _clickAction.Enable();
                _cancelAction.Enable();
            }
            else
            {
                // Assuming standard naming conventions
                var map = inputActions.FindActionMap("Gameplay"); // Or "Player"
                if (map != null)
                {
                    _pointAction = map.FindAction("Point");
                    _clickAction = map.FindAction("Click"); // Or "Fire"
                    _cancelAction = map.FindAction("Cancel");
                }
            }

            if (_clickAction != null)
            {
                _clickAction.performed += OnClickPerformed;
            }
            if (_cancelAction != null)
            {
                _cancelAction.performed += OnCancelPerformed;
            }
        }

        private void OnEnable()
        {
            _pointAction?.Enable();
            _clickAction?.Enable();
            _cancelAction?.Enable();
        }

        private void OnDisable()
        {
            _pointAction?.Disable();
            _clickAction?.Disable();
            _cancelAction?.Disable();
        }

        private void Update()
        {
            HandleHover();
        }

        private void HandleHover()
        {
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null || _pointAction == null) return;

            // Check if pointer is over UI
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }

            Vector2 mousePos = _pointAction.ReadValue<Vector2>();
            Ray ray = _mainCamera.ScreenPointToRay(mousePos);

            if (UnityEngine.Physics.Raycast(ray, out RaycastHit groundHit, 100f, groundLayer))
            {
                // Pass the raw world point. The Controller decides if it's a Triangle or Vertex.
                OnGroundHover?.Invoke(groundHit.point);
            }
        }

        private void OnClickPerformed(InputAction.CallbackContext context)
        {
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null || _pointAction == null) return;

            // Check if pointer is over UI
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }

            Vector2 mousePos = _pointAction.ReadValue<Vector2>();
            Ray ray = _mainCamera.ScreenPointToRay(mousePos);

            // Ensure layers are not Nothing (0) if they haven't been set
            if (unitLayer.value == 0) unitLayer = LayerMask.GetMask("Default");
            if (groundLayer.value == 0) groundLayer = LayerMask.GetMask("Default");

            // 1. Check for Unit Click (Higher Priority)
            // 任务 09：是否吞掉单位点击**只**由输入模式状态机决定（纯视图路由），
            // 与战斗逻辑权限无关；Logic 会在命令采纳时重新校验控制权。
            if (!SuppressUnitClicks && UnityEngine.Physics.Raycast(ray, out RaycastHit unitHit, 100f, unitLayer))
            {
                var unit = unitHit.collider.GetComponentInParent<CombatUnit>();
                if (unit != null)
                {
                    OnUnitClick?.Invoke(unit);
                    return; // Stop propagation if unit clicked
                }
            }

            // 2. Check for Ground Click
            if (SuppressWorldClicks) return;

            if (UnityEngine.Physics.Raycast(ray, out RaycastHit groundHit, 100f, groundLayer))
            {
                OnGroundClick?.Invoke(groundHit.point);
            }
        }

        private void OnCancelPerformed(InputAction.CallbackContext context)
        {
            // If pointer is over UI, let UI handle right-click (e.g., timeline cancel/delete)
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }
            OnCancel?.Invoke();
        }
    }
}

#endif
