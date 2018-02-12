﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Assets.Scripts.Records;
using UnityEngine;

public class BombCommander : ICommandResponder
{
    #region Constructors
    public BombCommander(Bomb bomb)
    {
        Bomb = bomb;
        timerComponent = Bomb.GetTimer();
        widgetManager = Bomb.WidgetManager;
        Selectable = Bomb.GetComponent<Selectable>();
        FloatingHoldable = Bomb.GetComponent<FloatingHoldable>();
        _selectableManager = KTInputManager.Instance.SelectableManager;
        BombTimeStamp = DateTime.Now;
        bombStartingTimer = CurrentTimer;

	    if (FloatingHoldable == null)
	    {
		    _elevatorRoom = SceneManager.Instance.GameplayState.Room is ElevatorRoom;
		    if (_elevatorRoom)
		    {
			    _currentWall = CurrentElevatorWall.Back;
			    Camera.main.transform.localPosition = ElevatorCameraPositions[(int) _currentWall];
			    Camera.main.transform.localEulerAngles = ElevatorCameraRotations[(int) _currentWall];
		    }
	    }
    }
	#endregion

	#region Interface Implementation
	public void ReuseBombCommander(Bomb bomb)
	{
		Bomb = bomb;
		timerComponent = Bomb.GetTimer();
		widgetManager = Bomb.WidgetManager;
		Selectable = Bomb.GetComponent<Selectable>();
		FloatingHoldable = Bomb.GetComponent<FloatingHoldable>();
		_selectableManager = KTInputManager.Instance.SelectableManager;
		BombTimeStamp = DateTime.Now;
		bombStartingTimer = CurrentTimer;
		bombSolvableModules = 0;
		bombSolvedModules = 0;
	}
    
    public IEnumerator RespondToCommand(string userNickName, string message, ICommandResponseNotifier responseNotifier)
	{
        message = message.ToLowerInvariant();

        if(message.EqualsAny("hold","pick up"))
        {
            responseNotifier.ProcessResponse(CommandResponse.Start);

            IEnumerator holdCoroutine = HoldBomb(_heldFrontFace);
            while (holdCoroutine.MoveNext())
            {
                yield return holdCoroutine.Current;
            }

            responseNotifier.ProcessResponse(CommandResponse.EndNotComplete);
        }
        else if (message.EqualsAny("turn", "turn round", "turn around", "rotate", "flip", "spin"))
        {
            responseNotifier.ProcessResponse(CommandResponse.Start);

	        if (_elevatorRoom)
	        {
		        IEnumerator dropAllHoldables = MiscellaneousMessageResponder.DropAllHoldables();
		        while (dropAllHoldables.MoveNext())
			        yield return dropAllHoldables.Current;

				IEnumerator rotateCamera;
		        switch (_currentWall)
		        {
			        case CurrentElevatorWall.Right:
				        rotateCamera = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Back, 1, false, false);
				        _currentWall = CurrentElevatorWall.Back;
				        break;
			        case CurrentElevatorWall.Dropped:
					case CurrentElevatorWall.Back:
				        rotateCamera = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Left, 1, false, false);
				        _currentWall = CurrentElevatorWall.Left;
				        break;
			        case CurrentElevatorWall.Left:
				        rotateCamera = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Right, 1, false, false);
				        _currentWall = CurrentElevatorWall.Right;
				        break;
			        default: yield break;
		        }
		        while (rotateCamera.MoveNext())
			        yield return rotateCamera.Current;
	        }
	        else
	        {
		        IEnumerator holdCoroutine = HoldBomb(!_heldFrontFace);
		        while (holdCoroutine.MoveNext())
		        {
			        yield return holdCoroutine.Current;
		        }
	        }

	        responseNotifier.ProcessResponse(CommandResponse.EndNotComplete);
        }
        else if (message.EqualsAny("drop","let go","put down"))
        {
            responseNotifier.ProcessResponse(CommandResponse.Start);

            IEnumerator letGoCoroutine = LetGoBomb();
            while (letGoCoroutine.MoveNext())
            {
                yield return letGoCoroutine.Current;
            }

            responseNotifier.ProcessResponse(CommandResponse.EndNotComplete);
        }
        else if (Regex.IsMatch(message, "^(edgework( 45|-45)?)$") || 
                 Regex.IsMatch(message, "^(edgework( 45|-45)? )?(top|top right|right top|right|right bottom|bottom right|bottom|bottom left|left bottom|left|left top|top left|back|)$"))
        {
            responseNotifier.ProcessResponse(CommandResponse.Start);
            bool _45Degrees = Regex.IsMatch(message, "^(edgework(-45| 45)).*$");
            IEnumerator edgeworkCoroutine = ShowEdgework(message.Replace("edgework", "").Replace(" 45", "").Replace("-45","").Trim(), _45Degrees);
            while (edgeworkCoroutine.MoveNext())
            {
                yield return edgeworkCoroutine.Current;
            }

            responseNotifier.ProcessResponse(CommandResponse.EndNotComplete);
        }
        else
        {
            responseNotifier.ProcessResponse(CommandResponse.NoResponse);
        }
    }
    #endregion

    #region Helper Methods
    public IEnumerator HoldBomb(bool frontFace = true)
    {
	    if (_elevatorRoom && _currentWall == CurrentElevatorWall.Dropped)
	    {
		    IEnumerator dropAllHoldables = MiscellaneousMessageResponder.DropAllHoldables();
		    while (dropAllHoldables.MoveNext())
			    yield return dropAllHoldables.Current;

		    IEnumerator holdBomb = DoElevatorCameraRotate(CurrentElevatorWall.Dropped, CurrentElevatorWall.Back, 1, false, false);
		    while (holdBomb.MoveNext())
			    yield return holdBomb.Current;
			_currentWall = CurrentElevatorWall.Back;
	    }
		else if (FloatingHoldable != null)
	    {
		    FloatingHoldable.HoldStateEnum holdState = FloatingHoldable.HoldState;
		    bool doForceRotate = false;

		    if (holdState != FloatingHoldable.HoldStateEnum.Held)
		    {
			    SelectObject(Selectable);
			    doForceRotate = true;
			    BombMessageResponder.moduleCameras?.ChangeBomb(this);
		    }
		    else if (frontFace != _heldFrontFace)
		    {
			    doForceRotate = true;
		    }

		    if (doForceRotate)
		    {
			    float holdTime = FloatingHoldable.PickupTime;
			    IEnumerator forceRotationCoroutine = ForceHeldRotation(frontFace, holdTime);
			    while (forceRotationCoroutine.MoveNext())
			    {
				    yield return forceRotationCoroutine.Current;
			    }
		    }
	    }
    }

    public IEnumerator TurnBomb()
    {
        IEnumerator holdBombCoroutine = HoldBomb(!_heldFrontFace);
        while (holdBombCoroutine.MoveNext())
        {
            yield return holdBombCoroutine.Current;
        }
    }

    public IEnumerator LetGoBomb()
    {
	    if (_elevatorRoom && _currentWall != CurrentElevatorWall.Dropped)
	    {
		    IEnumerator bombDrop = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Dropped, 1, false, false);
		    while (bombDrop.MoveNext())
			    yield return bombDrop.Current;
		    _currentWall = CurrentElevatorWall.Dropped;
	    }
		else if (FloatingHoldable != null)
	    {
		    if (FloatingHoldable.HoldState != FloatingHoldable.HoldStateEnum.Held) yield break;

		    IEnumerator turnBombCoroutine = HoldBomb(true);
		    while (turnBombCoroutine.MoveNext())
		    {
			    yield return turnBombCoroutine.Current;
		    }

		    while (FloatingHoldable.HoldState == FloatingHoldable.HoldStateEnum.Held)
		    {
			    DeselectObject(Selectable);
			    yield return new WaitForSeconds(0.1f);
		    }
	    }
    }

    public IEnumerator ShowEdgework(string edge, bool _45Degrees)
    {
	    if (FloatingHoldable == null)
	    {
		    if (!_elevatorRoom) yield break;

		    IEnumerator showEdgework = MiscellaneousMessageResponder.DropAllHoldables();
		    while (showEdgework.MoveNext())
			    yield return showEdgework.Current;

		    CurrentElevatorWall currentWall = _currentWall == CurrentElevatorWall.Dropped ? CurrentElevatorWall.Back : _currentWall;
		    if (edge == "" || edge == "left")
		    {
			    showEdgework = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Left, 1, false, true);
			    _currentWall = CurrentElevatorWall.Left;
				while (showEdgework.MoveNext())
				    yield return showEdgework.Current;
			    yield return new WaitForSeconds(3);
		    }
		    if (edge == "" || edge == "back")
		    {
			    showEdgework = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Back, 1, edge == "", true);
			    _currentWall = CurrentElevatorWall.Back;
			    while (showEdgework.MoveNext())
				    yield return showEdgework.Current;
			    yield return new WaitForSeconds(3);
		    }
		    if (edge == "" || edge == "right")
		    {
			    showEdgework = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Right, 1, edge == "", true);
			    _currentWall = CurrentElevatorWall.Right;
			    while (showEdgework.MoveNext())
				    yield return showEdgework.Current;
			    yield return new WaitForSeconds(3);
		    }
		    showEdgework = DoElevatorCameraRotate(_currentWall, currentWall, 1, true, false);
		    _currentWall = currentWall;
		    while (showEdgework.MoveNext())
			    yield return showEdgework.Current;
		    yield break;
	    }

	    if (edge == "back") yield break;
        BombMessageResponder.moduleCameras?.Hide();

        IEnumerator holdCoroutine = HoldBomb(_heldFrontFace);
        while (holdCoroutine.MoveNext())
        {
            yield return holdCoroutine.Current;
        }
        IEnumerator returnToFace;
        float offset = _45Degrees ? 0.0f : 45.0f;

        if (edge == "" || edge == "right")
        {
            IEnumerator firstEdge = DoFreeYRotate(0.0f, 0.0f, 90.0f, 90.0f, 0.3f);
            while (firstEdge.MoveNext())
            {
                yield return firstEdge.Current;
            }
            yield return new WaitForSeconds(2.0f);
        }

        if ((edge == "" && _45Degrees) || edge == "bottom right" || edge == "right bottom")
        {
            IEnumerator firstSecondEdge = edge == ""
                ? DoFreeYRotate(90.0f, 90.0f, 45.0f, 90.0f, 0.3f)
                : DoFreeYRotate(0.0f, 0.0f, 45.0f, 90.0f, 0.3f);
            while (firstSecondEdge.MoveNext())
            {
                yield return firstSecondEdge.Current;
            }
            yield return new WaitForSeconds(0.5f);
        }

        if (edge == "" || edge == "bottom")
        {

            IEnumerator secondEdge = edge == ""
                ? DoFreeYRotate(45.0f + offset, 90.0f, 0.0f, 90.0f, 0.3f)
                : DoFreeYRotate(0.0f, 0.0f, 0.0f, 90.0f, 0.3f);
            while (secondEdge.MoveNext())
            {
                yield return secondEdge.Current;
            }
            yield return new WaitForSeconds(2.0f);
        }

        if ((edge == "" && _45Degrees) || edge == "bottom left" || edge == "left bottom")
        {
            IEnumerator secondThirdEdge = edge == ""
                ? DoFreeYRotate(0.0f, 90.0f, -45.0f, 90.0f, 0.3f)
                : DoFreeYRotate(0.0f, 0.0f, -45.0f, 90.0f, 0.3f);
            while (secondThirdEdge.MoveNext())
            {
                yield return secondThirdEdge.Current;
            }
            yield return new WaitForSeconds(0.5f);
        }

        if (edge == "" || edge == "left")
        {
            IEnumerator thirdEdge = edge == ""
                ? DoFreeYRotate(-45.0f + offset, 90.0f, -90.0f, 90.0f, 0.3f)
                : DoFreeYRotate(0.0f, 0.0f, -90.0f, 90.0f, 0.3f);
            while (thirdEdge.MoveNext())
            {
                yield return thirdEdge.Current;
            }
            yield return new WaitForSeconds(2.0f);
        }

        if ((edge == "" && _45Degrees) || edge == "top left" || edge == "left top")
        {
            IEnumerator thirdFourthEdge = edge == ""
                ? DoFreeYRotate(-90.0f, 90.0f, -135.0f, 90.0f, 0.3f)
                : DoFreeYRotate(0.0f, 0.0f, -135.0f, 90.0f, 0.3f);
            while (thirdFourthEdge.MoveNext())
            {
                yield return thirdFourthEdge.Current;
            }
            yield return new WaitForSeconds(0.5f);
        }

        if (edge == "" || edge == "top")
        {
            IEnumerator fourthEdge = edge == ""
                ? DoFreeYRotate(-135.0f + offset, 90.0f, -180.0f, 90.0f, 0.3f)
                : DoFreeYRotate(0.0f, 0.0f, -180.0f, 90.0f, 0.3f);
            while (fourthEdge.MoveNext())
            {
                yield return fourthEdge.Current;
            }
            yield return new WaitForSeconds(2.0f);
        }

        if ((edge == "" && _45Degrees) || edge == "top right" || edge == "right top")
        {
            IEnumerator fourthFirstEdge = edge == ""
                ? DoFreeYRotate(-180.0f, 90.0f, -225.0f, 90.0f, 0.3f)
                : DoFreeYRotate(0.0f, 0.0f, -225.0f, 90.0f, 0.3f);
            while (fourthFirstEdge.MoveNext())
            {
                yield return fourthFirstEdge.Current;
            }
            yield return new WaitForSeconds(0.5f);
        }

        switch (edge)
        {
            case "right":
                returnToFace = DoFreeYRotate(90.0f, 90.0f, 0.0f, 0.0f, 0.3f);
                break;
            case "right bottom":
            case "bottom right":
                returnToFace = DoFreeYRotate(45.0f, 90.0f, 0.0f, 0.0f, 0.3f);
                break;
            case "bottom":
                returnToFace = DoFreeYRotate(0.0f, 90.0f, 0.0f, 0.0f, 0.3f);
                break;
            case "left bottom":
            case "bottom left":
                returnToFace = DoFreeYRotate(-45.0f, 90.0f, 0.0f, 0.0f, 0.3f);
                break;
            case "left":
                returnToFace = DoFreeYRotate(-90.0f, 90.0f, 0.0f, 0.0f, 0.3f);
                break;
            case "left top":
            case "top left":
                returnToFace = DoFreeYRotate(-135.0f, 90.0f, 0.0f, 0.0f, 0.3f);
                break;
            case "top":
                returnToFace = DoFreeYRotate(-180.0f, 90.0f, 0.0f, 0.0f, 0.3f);
                break;
            default:
            case "top right":
            case "right top":
                returnToFace = DoFreeYRotate(-225.0f + offset, 90.0f, 0.0f, 0.0f, 0.3f);
                break;
        }
        
        while (returnToFace.MoveNext())
        {
            yield return returnToFace.Current;
        }

        BombMessageResponder.moduleCameras?.Show();
    }

	public IEnumerable<Dictionary<string, T>> QueryWidgets<T>(string queryKey, string queryInfo = null)
	{
	    return widgetManager.GetWidgetQueryResponses(queryKey, queryInfo).Select(str => Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, T>>(str));
	}

	public void FillEdgework(bool silent = false)
	{
		List<string> edgework = new List<string>();
		Dictionary<string, string> portNames = new Dictionary<string, string>()
		{
			{ "RJ45", "RJ" },
			{ "StereoRCA", "RCA" }
		};

		var batteries = QueryWidgets<int>(KMBombInfo.QUERYKEY_GET_BATTERIES);
		edgework.Add(string.Format("{0}B {1}H", batteries.Sum(x => x["numbatteries"]), batteries.Count()));

		edgework.Add(QueryWidgets<string>(KMBombInfo.QUERYKEY_GET_INDICATOR).OrderBy(x => x["label"]).Select(x => (x["on"] == "True" ? "*" : "") + x["label"]).Join());

		edgework.Add(QueryWidgets<List<string>>(KMBombInfo.QUERYKEY_GET_PORTS).Select(x => x["presentPorts"].Select(port => portNames.ContainsKey(port) ? portNames[port] : port).OrderBy(y => y).Join(", ")).Select(x => x == "" ? "Empty" : x).Select(x => "[" + x + "]").Join(" "));
		
		edgework.Add(QueryWidgets<string>(KMBombInfo.QUERYKEY_GET_SERIAL_NUMBER).First()["serial"]);
		
		string edgeworkString = edgework.Where(str => str != "").Join(" // ");
		if (twitchBombHandle.edgeworkText.text == edgeworkString) return;

		twitchBombHandle.edgeworkText.text = edgeworkString;

        if(!silent)
	        IRCConnection.Instance.SendMessage(TwitchPlaySettings.data.BombEdgework, edgeworkString);
	}
	
    public IEnumerator Focus(Selectable selectable, float focusDistance, bool frontFace)
    {
	    if (FloatingHoldable != null)
	    {
		    IEnumerator holdCoroutine = HoldBomb(frontFace);
		    while (holdCoroutine.MoveNext())
		    {
			    yield return holdCoroutine.Current;
		    }

		    float focusTime = FloatingHoldable.FocusTime;
		    FloatingHoldable.Focus(selectable.transform, focusDistance, false, false, focusTime);
	    }
		else if(_elevatorRoom)
	    {
		    IEnumerator turnBomb = null;
		    int rotation = (int) Math.Round(selectable.transform.localEulerAngles.y, 0);
		    DebugHelper.Log($"selectable.name = {selectable.transform.name}");
		    DebugHelper.Log($"selectable position = {Math.Round(selectable.transform.localPosition.x, 3)},{Math.Round(selectable.transform.localPosition.y, 3)},{Math.Round(selectable.transform.localPosition.z, 3)}");
		    DebugHelper.Log($"selectable rotation = {Math.Round(selectable.transform.localEulerAngles.y, 3)}");

			switch (rotation)
			{
				case 90 when _currentWall != CurrentElevatorWall.Left:
					turnBomb = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Left, 1, false, false);
					_currentWall = CurrentElevatorWall.Left;
					break;
				case 180 when _currentWall != CurrentElevatorWall.Back:
					turnBomb = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Back, 1, false, false);
					_currentWall = CurrentElevatorWall.Back;
					break;
				case 270 when _currentWall != CurrentElevatorWall.Right:
					turnBomb = DoElevatorCameraRotate(_currentWall, CurrentElevatorWall.Right, 1, false, false);
					_currentWall = CurrentElevatorWall.Right;
					break;
			}
		    while (turnBomb != null && turnBomb.MoveNext())
			    yield return turnBomb.Current;
	    }

		
		selectable.HandleSelect(false);
        selectable.HandleInteract();
    }

    public IEnumerator Defocus(Selectable selectable, bool frontFace)
    {
        FloatingHoldable?.Defocus(false, false);
        selectable.HandleCancel();
        selectable.HandleDeselect();
        yield break;
    }

    public void RotateByLocalQuaternion(Quaternion localQuaternion)
    {
	    if (FloatingHoldable == null) return;
        Transform baseTransform = _selectableManager.GetBaseHeldObjectTransform();

        float currentZSpin = _heldFrontFace ? 0.0f : 180.0f;

        _selectableManager.SetControlsRotation(baseTransform.rotation * Quaternion.Euler(0.0f, 0.0f, currentZSpin) * localQuaternion);
        _selectableManager.HandleFaceSelection();
    }

	public void RotateCameraByLocalQuaternion(BombComponent bombComponent, Quaternion localQuaternion)
	{
		Transform twitchPlaysCameraTransform = bombComponent?.transform.Find("TwitchPlayModuleCamera");
		Camera cam = twitchPlaysCameraTransform?.GetComponentInChildren<Camera>();
		if (cam == null) return;

		int originalLayer = -1;
		for (int i = 0; i < 32 && originalLayer < 0; i++)
		{
			if ((cam.cullingMask & (1 << i)) != (1 << i)) continue;
			originalLayer = i;
		}

		int layer = localQuaternion == Quaternion.identity ? originalLayer : 31;

		foreach (Transform trans in bombComponent.gameObject.GetComponentsInChildren<Transform>(true))
		{
			trans.gameObject.layer = layer;
		}

		twitchPlaysCameraTransform.localRotation = Quaternion.Euler(_heldFrontFace ? -localQuaternion.eulerAngles : localQuaternion.eulerAngles);
	}

	public void CauseStrikesToExplosion(string reason)
    {
        for (int strikesToMake = StrikeLimit - StrikeCount; strikesToMake > 0; --strikesToMake)
        {
            CauseStrike(reason);
        }
    }

    public void CauseStrike(string reason)
    {
        StrikeSource strikeSource = new StrikeSource();
        strikeSource.ComponentType = Assets.Scripts.Missions.ComponentTypeEnum.Mod;
        strikeSource.InteractionType = Assets.Scripts.Records.InteractionTypeEnum.Other;
        strikeSource.Time = CurrentTimerElapsed;
        strikeSource.ComponentName = reason;

        RecordManager recordManager = RecordManager.Instance;
        recordManager.RecordStrike(strikeSource);

        Bomb.OnStrike(null);
    }

    private void SelectObject(Selectable selectable)
    {
        selectable.HandleSelect(true);
        _selectableManager.Select(selectable, true);
        _selectableManager.HandleInteract();
        selectable.OnInteractEnded();
    }

    private void DeselectObject(Selectable selectable)
    {
        _selectableManager.HandleCancel();
    }

    private IEnumerator ForceHeldRotation(bool frontFace, float duration)
    {
	    if (FloatingHoldable == null) yield break;
        Transform baseTransform = _selectableManager.GetBaseHeldObjectTransform();

        float oldZSpin = _heldFrontFace ? 0.0f : 180.0f;
        float targetZSpin = frontFace ? 0.0f : 180.0f;

        float initialTime = Time.time;
        while (Time.time - initialTime < duration)
        {
            float lerp = (Time.time - initialTime) / duration;
            float currentZSpin = Mathf.SmoothStep(oldZSpin, targetZSpin, lerp);

            Quaternion currentRotation = Quaternion.Euler(0.0f, 0.0f, currentZSpin);

            _selectableManager.SetZSpin(currentZSpin);
            _selectableManager.SetControlsRotation(baseTransform.rotation * currentRotation);
            _selectableManager.HandleFaceSelection();
            yield return null;
        }

        _selectableManager.SetZSpin(targetZSpin);
        _selectableManager.SetControlsRotation(baseTransform.rotation * Quaternion.Euler(0.0f, 0.0f, targetZSpin));
        _selectableManager.HandleFaceSelection();

        _heldFrontFace = frontFace;
    }
	
	private IEnumerator DoElevatorCameraRotate(CurrentElevatorWall currentWall, CurrentElevatorWall newWall, float duration, bool fromEdgework, bool toEdgework)
	{
		if (!_elevatorRoom) yield break;
		float initialTime = Time.time;
		Vector3 currentWallPosition = ElevatorCameraPositions[(int) currentWall];
		Vector3 currentWallRotation = fromEdgework ? ElevatorEdgeworkCameraRotations[(int) currentWall] : ElevatorCameraRotations[(int) currentWall];
		Vector3 newWallPosition = ElevatorCameraPositions[(int)newWall];
		Vector3 newWallRotation = toEdgework ? ElevatorEdgeworkCameraRotations[(int)newWall] : ElevatorCameraRotations[(int)newWall];
		Transform camera = Camera.main.transform;
		while ((Time.time - initialTime) < duration)
		{
			float lerp = (Time.time - initialTime) / duration;
			camera.localPosition = new Vector3(Mathf.SmoothStep(currentWallPosition.x, newWallPosition.x, lerp),
												Mathf.SmoothStep(currentWallPosition.y, newWallPosition.y, lerp),
												Mathf.SmoothStep(currentWallPosition.z, newWallPosition.z, lerp));
			camera.localEulerAngles = new Vector3(Mathf.SmoothStep(currentWallRotation.x, newWallRotation.x, lerp),
										Mathf.SmoothStep(currentWallRotation.y, newWallRotation.y, lerp),
										Mathf.SmoothStep(currentWallRotation.z, newWallRotation.z, lerp));
			yield return null;
		}
		camera.localPosition = newWallPosition;
		camera.localEulerAngles = newWallRotation;
	}

    private IEnumerator DoFreeYRotate(float initialYSpin, float initialPitch, float targetYSpin, float targetPitch, float duration)
    {
	    if (FloatingHoldable == null) yield break;
		if (!_heldFrontFace)
        {
            initialPitch *= -1;
            initialYSpin *= -1;
            targetPitch *= -1;
            targetYSpin *= -1;
        }

        float initialTime = Time.time;
        while (Time.time - initialTime < duration)
        {
            float lerp = (Time.time - initialTime) / duration;
            float currentYSpin = Mathf.SmoothStep(initialYSpin, targetYSpin, lerp);
            float currentPitch = Mathf.SmoothStep(initialPitch, targetPitch, lerp);

            Quaternion currentRotation = Quaternion.Euler(currentPitch, 0, 0) * Quaternion.Euler(0, currentYSpin, 0);
            RotateByLocalQuaternion(currentRotation);
            yield return null;
        }
        Quaternion target = Quaternion.Euler(targetPitch, 0, 0) * Quaternion.Euler(0, targetYSpin, 0);
        RotateByLocalQuaternion(target);
    }

    private void HandleStrikeChanges()
    {
        int strikeLimit = StrikeLimit;
        int strikeCount = Math.Min(StrikeCount, StrikeLimit);

        RecordManager RecordManager = RecordManager.Instance;
        GameRecord GameRecord = RecordManager.GetCurrentRecord();
        StrikeSource[] Strikes = GameRecord.Strikes;
        if (Strikes.Length != strikeLimit)
        {
            StrikeSource[] newStrikes = new StrikeSource[Math.Max(strikeLimit, 1)];
            Array.Copy(Strikes, newStrikes, Math.Min(Strikes.Length, newStrikes.Length));
            GameRecord.Strikes = newStrikes;
        }

        if (strikeCount == strikeLimit)
        {
            if (strikeLimit < 1)
            {
                Bomb.NumStrikesToLose = 1;
                strikeLimit = 1;
            }
            Bomb.NumStrikes = strikeLimit - 1;
            CommonReflectedTypeInfo.GameRecordCurrentStrikeIndexField.SetValue(GameRecord, strikeLimit - 1);
            CauseStrike("Strike count / limit changed.");
        }
        else
        {
            Debug.Log(string.Format("[Bomb] Strike from TwitchPlays! {0} / {1} strikes", StrikeCount, StrikeLimit));
            CommonReflectedTypeInfo.GameRecordCurrentStrikeIndexField.SetValue(GameRecord, strikeCount);
            //MasterAudio.PlaySound3DAtTransformAndForget("strike", base.transform, 1f, null, 0f, null);
            float[] rates = { 1, 1.25f, 1.5f, 1.75f, 2 };
            timerComponent.SetRateModifier(rates[Math.Min(strikeCount, 4)]);
            Bomb.StrikeIndicator.StrikeCount = strikeCount;
        }
    }

    public bool IsSolved => Bomb.IsSolved();

    public float CurrentTimerElapsed => timerComponent.TimeElapsed;

    public float CurrentTimer
    {
        get => timerComponent.TimeRemaining;
        set => timerComponent.TimeRemaining = (value < 0) ? 0 : value;
    }

    public string CurrentTimerFormatted => timerComponent.GetFormattedTime(CurrentTimer, true);

    public string StartingTimerFormatted => timerComponent.GetFormattedTime(bombStartingTimer, true);

    public string GetFullFormattedTime => Math.Max(CurrentTimer, 0).FormatTime();

    public string GetFullStartingTime => Math.Max(bombStartingTimer, 0).FormatTime();

    public int StrikeCount
    {
        get => Bomb.NumStrikes;
        set
        {
            if (value < 0) value = 0;   //Simon says is unsolvable with less than zero strikes.
            Bomb.NumStrikes = value;
            HandleStrikeChanges();
        }
    }

    public int StrikeLimit
    {
        get => Bomb.NumStrikesToLose;
        set { Bomb.NumStrikesToLose = value; HandleStrikeChanges(); }
    }

    public int NumberModules => bombSolvableModules;

    private static string[] solveBased = new string[] { "MemoryV2", "SouvenirModule", "TurnTheKeyAdvanced" };
	private bool removedSolveBasedModules = false;
	public void RemoveSolveBasedModules()
	{
		if (removedSolveBasedModules) return;
		removedSolveBasedModules = true;

		foreach (KMBombModule module in Bomb.GetComponentsInChildren<KMBombModule>())
		{
			if (solveBased.Contains(module.ModuleType))
			{
				module.HandlePass();
			}
		}
	}
	#endregion

	public Bomb Bomb = null;
    public Selectable Selectable = null;
    public FloatingHoldable FloatingHoldable = null;
    public DateTime BombTimeStamp;

    private SelectableManager _selectableManager = null;

    public TwitchBombHandle twitchBombHandle = null;
    public TimerComponent timerComponent = null;
	public WidgetManager widgetManager = null;
	public int bombSolvableModules;
    public int bombSolvedModules;
    public float bombStartingTimer;

    private bool _heldFrontFace = true;
	private bool _elevatorRoom = false;
	private bool _elevatorDropped = true;
	private CurrentElevatorWall _currentWall;

	private enum CurrentElevatorWall
	{
		Left,
		Back,
		Right,
		Dropped
	}

	private Vector3[] ElevatorCameraRotations =
	{
		new Vector3(7, -89, 21),
		new Vector3(-8, 0, 0),
		new Vector3(5, 88, -21),
		Vector3.zero
	};

	private Vector3[] ElevatorEdgeworkCameraRotations =
	{
		new Vector3(20, -85, 20),
		new Vector3(0, 0, 0),
		new Vector3(20, 85, -20),
		Vector3.zero
	};

	private Vector3[] ElevatorCameraPositions =
	{
		new Vector3(0.5f, 0.75f, 1.15f),
		new Vector3(-0.15f, 0.75f, 0.75f),
		new Vector3(-0.5f, 0.75f, 1.25f),
		Vector3.zero
	};
}
