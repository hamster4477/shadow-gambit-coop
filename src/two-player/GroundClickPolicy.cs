// Aiming can stay active during ordinary contextual movement. The controller's
// selected skill distinguishes that mode from an explicitly selected ability.
internal static class GroundClickPolicy
{
    internal static bool Allows(bool aiming, Skill.SkillType? selected, Skill.SkillType defaultSkill, bool defaultInstance)
    {
        return !aiming || !selected.HasValue || defaultInstance || selected == defaultSkill
            || selected == Skill.SkillType.None || selected == Skill.SkillType.GoTo;
    }
}
