using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenUtau.Core {
    public abstract class UCommand {
        public virtual bool Silent => false;
        public virtual ValidateOptions ValidateOptions => default;
        public abstract void Execute();
        public abstract void Unexecute();
        public virtual bool CanMerge(IList<UCommand> commands) => false;
        public virtual UCommand Merge(IList<UCommand> commands) => throw new NotImplementedException();
        public abstract override string ToString();
    }

    public class UCommandGroup {
        public string? NameKey;
        public bool DeferValidate;
        public List<UCommand> Commands;
        public UCommandGroup(string? nameKey, bool deferValidate) {
            NameKey = nameKey;
            DeferValidate = deferValidate;
            Commands = new List<UCommand>();
        }
        public void Merge() {
            if (Commands.Count > 0 && Commands.Last().CanMerge(Commands)) {
                var merged = Commands.Last().Merge(Commands);
                Commands.Clear();
                Commands.Add(merged);
            }
        }
        /// <summary>
        /// Options to validate a deferred group with.
        /// When every command of the group skips the phonemizer, validating once with the same
        /// options is equivalent to validating after each command, but only once. This matters for
        /// batches like vibrato or pitch edits, where re-running the phonemizer is pure waste and
        /// would also leave the render phrases out of date for the pre-render that follows.
        /// Any other batch may have changed lyrics, timing or a part other commands depend on, so it
        /// falls back to a full validation (the default options).
        /// </summary>
        public ValidateOptions GetDeferredValidateOptions() {
            if (Commands.Count == 0) {
                return default;
            }
            var options = Commands[0].ValidateOptions;
            if (options.Part == null || !options.SkipPhonemizer) {
                return default;
            }
            foreach (var cmd in Commands) {
                var other = cmd.ValidateOptions;
                if (other.SkipTiming != options.SkipTiming
                    || !ReferenceEquals(other.Part, options.Part)
                    || other.SkipPhonemizer != options.SkipPhonemizer
                    || other.SkipPhoneme != options.SkipPhoneme
                    || other.SkipRenderPhrase != options.SkipRenderPhrase) {
                    return default;
                }
            }
            return options;
        }
        public override string ToString() { return Commands.Count == 0 ? "No op" : Commands.First().ToString(); }
    }

    public interface ICmdSubscriber {
        void OnNext(UCommand cmd, bool isUndo);
    }
}
