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
        internal void AddDamageAttack(string attacker, string skillType, bool critical, bool lucky, int damage, DateTime time)
        {
            try
            {
                if (ActGlobals.oFormActMain.SetEncounter(time, attacker, "Enemy"))
                {
                    MasterSwing ms = new(
                        (int)SwingTypeEnum.Melee,
                        critical,
                        lucky ? "Direct" : "",
                        new Dnum(damage),
                        time,
                        ActGlobals.oFormActMain.GlobalTimeSorter,
                        skillType,
                        attacker,
                        "Skill",
                        "Enemy");

                    ActGlobals.oFormActMain.AddCombatAction(ms);
                }
            } catch
            {
                Log("Error sending ACT Damage Attack.");
            }
        }
    }
}
