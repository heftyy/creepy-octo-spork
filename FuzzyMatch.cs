// I initially based this on 
// https://blog.forrestthewoods.com/reverse-engineering-sublime-text-s-fuzzy-match-4cffeed33fdb
// https://medium.com/@Srekel/implementing-a-fuzzy-search-algorithm-for-the-debuginator-cacc349e6c55
// and ended up making quite a few modifications to the code to matches that i liked better and improved performance

static class FileSearch
{
    static int CalculateScore(string pattern, string str, int pattern_index, int str_index, int filename_start_index, int[] matches)
    {
        const int sequential_bonus = 20;                // bonus for adjacent matches
        const int separator_bonus = 20;                 // bonus if match occurs after a separator
        const int camel_bonus = 25;                     // bonus if match is uppercase and prev is lower
        const int first_letter_bonus = 25;              // bonus if the first letter is matched
        const int filename_bonus = 15;                  // bonus if the match is in the filename instead of the path

        const int leading_letter_penalty = -2;          // penalty applied for every letter in str before the first match
        const int unmatched_letter_penalty = -1;        // penalty for every letter that doesn't matter

        // Initialize score
        int out_score = 0;

        // Prioritize cpp files 
        if (str[str.Length - 3] == 'c' && str[str.Length - 2] == 'p' && str[str.Length - 1] == 'p')
        {
            out_score += 3;
        }

        int matches_in_filename = 0;
        int first_match_in_filename = 0;

        // Apply ordering bonuses
        for (int i = 0; i < matches.Length; ++i)
        {
            int currIdx = matches[i];
            if (currIdx <= 0)
                break;

            if (i > 0)
            {
                int prevIdx = matches[i - 1];

                // Sequential
                if (currIdx == (prevIdx + 1))
                    out_score += sequential_bonus;
            }

            // Check for bonuses based on neighbor character value
            if (currIdx > 0)
            {
                // Camel case
                char neighbor = str[currIdx - 1];
                char curr = str[currIdx];
                if ((Char.IsLower(neighbor) || neighbor == '\\' || neighbor == '/') && Char.IsUpper(curr))
                    out_score += camel_bonus;

                // Separator
                bool neighborSeparator = neighbor == '_' || neighbor == ' ' || neighbor == '\\' || neighbor == '/';
                if (neighborSeparator)
                    out_score += separator_bonus;
            }

            if (currIdx >= filename_start_index)
            {
                if (first_match_in_filename == 0)
                {
                    first_match_in_filename = currIdx;
                }

                // Bonus for matching the filename
                out_score += filename_bonus;
                if (currIdx == filename_start_index)
                {
                    // First letter
                    out_score += first_letter_bonus;
                }
                ++matches_in_filename;
            }
        }

        // Apply leading letter penalty
        out_score += Math.Min(leading_letter_penalty * (first_match_in_filename - filename_start_index), 0);

        // Apply unmatched penalty
        int unmatched = str.Length - filename_start_index - matches_in_filename;
        out_score += Math.Min(unmatched_letter_penalty * unmatched, 0);

        return out_score;
    }

    struct PatternMatch
    {
        public int m_Score;
        public int[] m_Matches;
    }

    static bool FuzzyMatch(string pattern, string str, ref int out_score, ref List<int> out_matches, int filename_start_index)
    {
        string pattern_lower = pattern.ToLower();
        string str_lower = str.ToLower();

        PatternMatch[] pattern_scores = new PatternMatch[pattern.Length];
        int[] matches = new int[pattern.Length];

        // Loop through pattern and str looking for a match
        for (int pattern_index = 0; pattern_index < pattern.Length; ++pattern_index)
        {
            Array.Clear(matches, 0, matches.Length);
            pattern_scores[pattern_index].m_Score = 0;

            int pattern_start = pattern_index;
            int match_length = 0;

            for (int str_index = 0; str_index < str.Length; ++str_index)
            {
                int search_pattern_index = pattern_index;
                int search_str_index = str_index;

                for (int i = 0; pattern_lower[search_pattern_index] == str_lower[search_str_index]; ++i)
                {
                    matches[i] = search_str_index;

                    search_pattern_index = pattern_index + i + 1;
                    search_str_index = str_index + i + 1;

                    if (search_pattern_index >= pattern.Length || search_str_index >= str.Length)
                    {
                        break;
                    }
                }

                if (search_pattern_index - pattern_index > 1)
                {
                    int match_score = CalculateScore(pattern, str, pattern_index, str_index, filename_start_index, matches);
                    if (match_score > pattern_scores[pattern_index].m_Score)
                    {
                        pattern_scores[pattern_index].m_Score = match_score;
                        pattern_scores[pattern_index].m_Matches = matches;
                        match_length = search_pattern_index - pattern_index;
                    }
                    // Search for better match for the pattern in the string
                    pattern_index = pattern_start;
                }
            }

            if (pattern_scores[pattern_index].m_Score > 0)
            {
                pattern_index += match_length - 1;
            }
        }

        foreach (PatternMatch match in pattern_scores)
        {
            if (match.m_Score > 0)
            {
                out_score += match.m_Score;
                out_matches.AddRange(match.m_Matches);
            }
        }

        return out_score > 0;
    }

    public class MatchScoreComparer : IComparer<ProjectScanner.SEntry>
    {
        public int Compare(ProjectScanner.SEntry lhs, ProjectScanner.SEntry rhs)
        {
            return rhs.MatchScore.CompareTo(lhs.MatchScore);
        }
    }

    /// <exception cref="OperationCanceledException">Throws when the task is cancelled</exception>
    public static List<ProjectScanner.SEntry> ApplyFileFilterExperimental(string expression, List<ProjectScanner.SEntry> input_files, CancellationToken token)
    {
        if (expression.Length == 0)
            return input_files;

        List<ProjectScanner.SEntry> result = new List<ProjectScanner.SEntry>();
        MatchScoreComparer match_score_comparer = new MatchScoreComparer();

        expression = string.Join("", expression.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

        foreach (ProjectScanner.SEntry file in input_files)
        {
            if (token.IsCancellationRequested)
                token.ThrowIfCancellationRequested();

            int score = 0;
            List<int> matches = new List<int>();
            FuzzyMatch(expression, file.Path, ref score, ref matches, file.Path.Length - file.Name.Length);

            if (score > 0)
            {
                file.SetMatchScore(score);
                result.Add(file);
            }
        }

        result.Sort(match_score_comparer);

        return result;
    }
}
