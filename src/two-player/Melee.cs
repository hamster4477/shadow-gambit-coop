using System.Globalization;
using Mimimi.MiScript;
using UnityEngine;

internal static class Melee
{
    internal static Skill.SkillType Special(MiCharacter actor)
    {
        if (actor == null || !actor.bControllerPlayer()) return Skill.SkillType.None;
        foreach (var skill in Specials)
            if (actor.controller.getSkill(skill) != null) return skill;
        return Skill.SkillType.None;
    }

    private static readonly Skill.SkillType[] Specials =
        { Skill.SkillType.BlinkKill, Skill.SkillType.SnipeKill, Skill.SkillType.Gun };

    internal static Skill.SkillType Decode(string key) => key switch {
        "K" or "KR" => Skill.SkillType.Knockout, "L" or "LR" => Skill.SkillType.Kill,
        "BlinkKill" => Skill.SkillType.BlinkKill, "SnipeKill" => Skill.SkillType.SnipeKill,
        "SnipeKnockout" => Skill.SkillType.SnipeKnockout, "BlinkKnockout" => Skill.SkillType.BlinkKnockout,
        "BlinkKillLong" => Skill.SkillType.BlinkKillLong, "BlinkKnockoutLong" => Skill.SkillType.BlinkKnockoutLong,
        "ThrowNoise" => Skill.SkillType.ThrowNoise, "ThrowBush" => Skill.SkillType.ThrowBush,
        "Gun" => Skill.SkillType.Gun, _ => Skill.SkillType.None
    };

    internal static string GuardId(MiCharacter actor) =>
        actor.guid.iValue.ToString(CultureInfo.InvariantCulture);

    internal static MiCharacter Pick()
    {
        var cam = Camera.main;
        if (cam == null) return null;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        // Only the first visible hit counts; never pick a target through a wall.
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f)) return null;
        var actor = hit.collider.GetComponentInParent<MiCharacter>();
        return actor != null && actor.bControllerAI() && !actor.guid.isEmpty() ? actor : null;
    }

    internal static MiCharacter Resolve(string id)
    {
        MiCharacter found = null;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MiCharacter>())
        {
            if (!c.bControllerAI() || c.guid.isEmpty() || GuardId(c) != id) continue;
            if (found != null) return null;
            found = c;
        }
        return found;
    }

    internal static bool Point(Skill.SkillType skill) => skill is Skill.SkillType.ThrowNoise or Skill.SkillType.ThrowBush;

    internal static bool Contact(Skill.SkillType skill) => skill == Skill.SkillType.Kill || skill == Skill.SkillType.Knockout;

    internal static bool CanUse(MiCharacter actor, MiCharacter target, Skill.SkillType skill, bool approach = false)
    {
        if (skill == Skill.SkillType.None || actor == null || target == null || actor == target ||
            !actor.bControllerPlayer() || !target.bControllerAI() ||
            actor.health.eHealthState != MiCharacterHealth.HealthState.Alive ||
            target.health.eHealthState != MiCharacterHealth.HealthState.Alive) return false;
        var ability = actor.controller.getSkill(skill);
        if (ability == null) return false;
        float range = approach ? float.MaxValue : Contact(skill) ? 4f :
            (ability.fRange > 0 ? ability.fRange : 4f);
        return Vector3.Distance(actor.transform.position, target.transform.position) <= range;
    }

    internal static void Execute(MiCharacter actor, MiCharacter target, Skill.SkillType skill)
    {
        MiScriptLibraryProject.CharacterExecuteSkill(actor, skill, target);
    }
}
