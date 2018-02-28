﻿using System.Collections;

public class NeedyKnobComponentSolver : ComponentSolver
{
    public NeedyKnobComponentSolver(BombCommander bombCommander, NeedyKnobComponent bombComponent) :
        base(bombCommander, bombComponent)
	{
		_pointingKnob = bombComponent.PointingKnob;
        modInfo = ComponentSolverFactory.GetModuleInfo("NeedyKnobComponentSolver");
    }

    protected override IEnumerator RespondToCommandInternal(string inputCommand)
    {
        string[] commandParts = inputCommand.ToLowerInvariant().Split(' ');

        if (commandParts.Length != 2)
        {
            yield break;
        }

        if (!commandParts[0].EqualsAny("rotate", "turn"))
        {
            yield break;
        }

        if (!int.TryParse(commandParts[1], out int totalTurnCount))
        {
            yield break;
        }

        totalTurnCount = totalTurnCount % 4;

        yield return "rotate";

        for (int turnCount = 0; turnCount < totalTurnCount; ++turnCount)
        {
	        yield return "trycancel";
            yield return DoInteractionClick(_pointingKnob);
        }
    }

    private PointingKnob _pointingKnob = null;
}
