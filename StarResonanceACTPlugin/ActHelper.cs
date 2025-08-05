using Advanced_Combat_Tracker;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StarResonanceACTPlugin
{
    internal class ActHelper
    {
        private StreamWriter logWriter;
        internal ActHelper(StreamWriter logger)
        {
            logWriter = logger;
        }

        private void Log(string message)
        {
            string timestamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            logWriter.WriteLine(timestamp);
            Console.WriteLine(timestamp);
        }

        internal void AddDamageAttack(string attacker, string target, string skillType, bool critical, bool lucky, bool heal, int damage, DateTime time)
        {
            try
            {
                if (ActGlobals.oFormActMain.SetEncounter(time, attacker, target))
                {
                    MasterSwing ms = new(
                        heal ? (int)SwingTypeEnum.Healing : (int)SwingTypeEnum.Melee,
                        critical,
                        lucky ? "Direct" : "",
                        new Dnum(damage),
                        time,
                        ActGlobals.oFormActMain.GlobalTimeSorter,
                        skillType,
                        attacker,
                        "Skill",
                        target);

                    ActGlobals.oFormActMain.AddCombatAction(ms);
                }
            }
            catch (Exception ex)
            {
                Log($"Error sending ACT Damage Attack: {ex.Message}");
            }
        }
    }
}
