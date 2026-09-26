using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ashen.Content
{
    /// <summary>One validation finding: file, JSON path inside the file, rule id (ValidationRules), message.</summary>
    public sealed class ValidationIssue
    {
        public ValidationIssue(string file, string path, string rule, string message)
        {
            File = file;
            Path = path;
            Rule = rule;
            Message = message;
        }

        public string File { get; }
        public string Path { get; }
        public string Rule { get; }
        public string Message { get; }

        public override string ToString() => string.Format(System.Globalization.CultureInfo.InvariantCulture, Ashen.Generated.ValidationMessages.IssueFormat, File, Path, Rule, Message);
    }

    public sealed class ValidationReport
    {
        private readonly List<ValidationIssue> _issues = new List<ValidationIssue>();

        public IReadOnlyList<ValidationIssue> Issues => _issues;
        public bool IsValid => _issues.Count == 0;
        public int FilesChecked { get; internal set; }

        internal void Add(string file, string path, string rule, string message) => _issues.Add(new ValidationIssue(file, path, rule, message));

        public IEnumerable<ValidationIssue> ByRule(string rule) => _issues.Where(i => i.Rule == rule);

        public string Summary(int max)
        {
            var sb = new StringBuilder();
            foreach (var issue in _issues.Take(max)) sb.AppendLine(issue.ToString());
            if (_issues.Count > max) sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, Ashen.Generated.ValidationMessages.MoreFormat, _issues.Count - max));
            return sb.ToString();
        }
    }
}
