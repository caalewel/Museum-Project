using UnityEngine;
using UnityEngine.InputSystem;

namespace SWGenerator{

	public class DummyPlayer : MonoBehaviour{

		[SerializeField] CharacterController characterController;
		[SerializeField] Camera mainCamera;
		[SerializeField] float moveSpeed = 10.0f;
		[SerializeField] float sprintMultiplier = 2.0f;
		[SerializeField] float lookSpeed = 10.0f;

		float gravity = -9.81f;
		float verticalVelocity = 0.0f;
		float xRotation = 0.0f;

		void Start(){
			if(characterController == null) TryGetComponent(out characterController);
			if(mainCamera == null) mainCamera = Camera.main;
			Cursor.lockState = CursorLockMode.Locked;
		}

		void Update(){
			if(Keyboard.current == null || Mouse.current == null) return;

			//LookAround
			float mouseX = Mouse.current.delta.ReadValue().x * lookSpeed * Time.deltaTime;
			float mouseY = Mouse.current.delta.ReadValue().y * lookSpeed * Time.deltaTime;
			xRotation -= mouseY;
			xRotation = Mathf.Clamp(xRotation, -80f, 80f);
			transform.Rotate(Vector3.up * mouseX);

			if(mainCamera != null){
				mainCamera.transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
			}

			//Move
			Vector3 move = Vector3.zero;
			if(Keyboard.current.wKey.isPressed) move += transform.forward;
			if(Keyboard.current.sKey.isPressed) move -= transform.forward;
			if(Keyboard.current.aKey.isPressed) move -= transform.right;
			if(Keyboard.current.dKey.isPressed) move += transform.right;
			move = move.normalized;

			//Gravity
			if(characterController.isGrounded) verticalVelocity = 0.0f;

			verticalVelocity += gravity * Time.deltaTime;
			move.y = verticalVelocity;

			//Sprint
			float currentSpeed = Keyboard.current.leftShiftKey.isPressed ? moveSpeed * sprintMultiplier : moveSpeed;
			characterController.Move(move * currentSpeed * Time.deltaTime);
		}
	}

}