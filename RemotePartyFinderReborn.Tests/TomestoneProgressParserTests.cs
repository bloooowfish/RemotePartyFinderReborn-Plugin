using System.Runtime.CompilerServices;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneProgressParserTests
{
    private static readonly TomestoneEncounterParams M2SProgressTarget = new(
        "dawntrail",
        "raids",
        "aac-heavyweight-m2-savage",
        TomestoneProgressKind.BossPercentage,
        ["red-hot-deep-blue"]);

    private static readonly TomestoneEncounterParams PhaseProgressTarget = new(
        "dawntrail",
        "ultimates",
        "target-ultimate",
        TomestoneProgressKind.PhaseProgress,
        ["target-phase"]);

    [Fact]
    public void ParsePhaseProgress_extracts_phase_progress_in_display_order()
    {
        var json = ReadFixture("progression-graph-ultimate-with-phases.json");

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        Assert.Collection(
            progress,
            phase =>
            {
                Assert.Equal("p1", phase.PhaseKey);
                Assert.Equal("P1", phase.PhaseName);
                Assert.Equal(1, phase.Order);
                Assert.Equal(12, phase.BossPercentage);
                Assert.Equal("P1 12%", phase.DisplayText);
                Assert.Equal("tomestone_api", phase.Source);
            },
            phase =>
            {
                Assert.Equal("p2", phase.PhaseKey);
                Assert.Equal("P2", phase.PhaseName);
                Assert.Equal(2, phase.Order);
                Assert.Equal(24, phase.BossPercentage);
                Assert.Equal("P2 24%", phase.DisplayText);
            },
            phase =>
            {
                Assert.Equal("p3", phase.PhaseKey);
                Assert.Equal("P3", phase.PhaseName);
                Assert.Equal(3, phase.Order);
                Assert.Equal(55.5, phase.BossPercentage);
                Assert.Equal("P3 55.5%", phase.DisplayText);
            },
            phase =>
            {
                Assert.Equal("p4", phase.PhaseKey);
                Assert.Equal("P4", phase.PhaseName);
                Assert.Equal(4, phase.Order);
                Assert.Equal(100, phase.BossPercentage);
                Assert.Equal("P4 100%", phase.DisplayText);
            });
    }

    [Fact]
    public void ParseProgress_reads_uncleared_ultimate_progress_from_profile_encounters()
    {
        const string json = """
        {
          "encounters": {
            "ultimate": [{
                "zoneName": "Futures Rewritten (Ultimate)",
                "progression": {
                  "mechanic": { "name": "Caloric Theory", "number": 7 },
                  "displayPercent": "41.13%"
                }
              }]
          }
        }
        """;

        var progress = TomestoneProgressParser.ParseProgress(
            json,
            new TomestoneEncounterParams(
                "dawntrail",
                "ultimates",
                "futures-rewritten-ultimate",
                TomestoneProgressKind.PhaseProgress,
                ["futures-rewritten-ultimate"]));

        var phase = Assert.Single(progress.Phases);
        Assert.Equal("mechanic-caloric-theory-7", phase.PhaseKey);
        Assert.Equal("Caloric Theory #7", phase.PhaseName);
        Assert.Equal(0, phase.Order);
        Assert.Equal(41.13, phase.BossPercentage);
        Assert.Equal("Caloric Theory #7 41.13%", phase.DisplayText);
    }

    [Fact]
    public void DescribeResponseShape_reports_only_schema_metadata()
    {
        const string json = """
        {
          "characterName": "Sensitive Player Name",
          "encounters": {
            "ultimates": [{
              "encounters": [{
                "zoneName": "Futures Rewritten (Ultimate)",
                "progression": {
                  "mechanic": { "name": "Caloric Theory", "number": 4 },
                  "displayPercent": "41.13%"
                }
              }]
            }]
          }
        }
        """;

        var shape = TomestoneProgressParser.DescribeResponseShape(json, new TomestoneEncounterParams(
            "dawntrail",
            "ultimates",
            "futures-rewritten-ultimate",
            TomestoneProgressKind.PhaseProgress,
            ["futures-rewritten-ultimate"]));

        Assert.Contains("rootKeys=[characterName,encounters]", shape);
        Assert.Contains("targetNodes=1", shape);
        Assert.Contains("displayPercent", shape);
        Assert.DoesNotContain("Sensitive Player Name", shape);
        Assert.DoesNotContain("41.13", shape);
    }

    [Fact]
    public void ParsePhaseProgress_extracts_best_phase_progress_from_activity_best_percent_rows()
    {
        const string json = """
        {
          "activity": {
            "activities": {
              "activities": {
                "paginator": {
                  "data": [
                    { "activity": { "bestPercent": "79.71% P5", "killsCount": 0 } },
                    { "activity": { "bestPercent": "3.20% P4", "killsCount": 0 } },
                    { "activity": { "bestPercent": "72.73% P3", "killsCount": 0 } },
                    { "activity": { "bestPercent": "21.40% P2", "killsCount": 0 } },
                    { "activity": { "bestPercent": "36.23% P5", "killsCount": 0 } },
                    { "activity": { "bestPercent": "0.00% P6", "killsCount": 1 } }
                  ]
                }
              }
            }
          }
        }
        """;

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        var phase = Assert.Single(progress);
        Assert.Equal("p5", phase.PhaseKey);
        Assert.Equal("P5", phase.PhaseName);
        Assert.Equal(5, phase.Order);
        Assert.Equal(36.23, phase.BossPercentage);
        Assert.Equal("P5 36.23%", phase.DisplayText);
    }

    [Fact]
    public void ParsePhaseProgress_ignores_non_phase_activity_best_percent_labels()
    {
        const string json = """
        {
          "activity": {
            "activities": {
              "activities": {
                "paginator": {
                  "data": [
                    { "activity": { "bestPercent": "41.13% P4", "killsCount": 0 } },
                    { "activity": { "bestPercent": "100.00% I1", "killsCount": 0 } }
                  ]
                }
              }
            }
          }
        }
        """;

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        var phase = Assert.Single(progress);
        Assert.Equal("p4", phase.PhaseKey);
        Assert.Equal("P4", phase.PhaseName);
        Assert.Equal(4, phase.Order);
        Assert.Equal(41.13, phase.BossPercentage);
        Assert.Equal("P4 41.13%", phase.DisplayText);
    }

    [Fact]
    public void ParsePhaseProgress_ignores_instance_graph_labels()
    {
        const string json = """
        {
          "data": {
            "graph": [
              { "phase": { "name": "P4", "number": 4 }, "bestPull": "41.13%" },
              { "phase": { "name": "I1", "number": 7 }, "bestPull": "100.00%" }
            ]
          }
        }
        """;

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        var phase = Assert.Single(progress);
        Assert.Equal("p4", phase.PhaseKey);
        Assert.Equal("P4", phase.PhaseName);
        Assert.Equal(4, phase.Order);
        Assert.Equal(41.13, phase.BossPercentage);
    }

    [Fact]
    public void ParsePhaseProgress_returns_empty_for_empty_or_missing_graph()
    {
        Assert.Empty(TomestoneProgressParser.ParsePhaseProgress(ReadFixture("progression-graph-empty.json")));
        Assert.Empty(TomestoneProgressParser.ParsePhaseProgress("""{"data":{"notGraph":[]}}"""));
    }

    [Fact]
    public void ParsePhaseProgress_ignores_plain_percentile_rank_style_fields()
    {
        var json = ReadFixture("progression-graph-malformed-shape.json");

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        Assert.Empty(progress);
    }

    [Fact]
    public void ParsePhaseProgress_ignores_bare_name_percent_aggregate_rows()
    {
        const string json = """
        [
          { "name": "Overall", "percent": "12%" }
        ]
        """;

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        Assert.Empty(progress);
    }

    [Fact]
    public void Preserves_phase_order_when_duplicate_improves_progress()
    {
        const string json = """
        [
          { "phaseName": "Phase Alpha", "order": 1, "bestPull": "55%" },
          { "phaseName": "Phase Beta", "order": 2, "bestPull": "33%" },
          { "phaseName": "Phase Alpha", "boss_percentage": "12%" }
        ]
        """;

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        Assert.Collection(
            progress,
            phase =>
            {
                Assert.Equal("phase-alpha", phase.PhaseKey);
                Assert.Equal("Phase Alpha", phase.PhaseName);
                Assert.Equal(1, phase.Order);
                Assert.Equal(12, phase.BossPercentage);
            },
            phase =>
            {
                Assert.Equal("phase-beta", phase.PhaseKey);
                Assert.Equal("Phase Beta", phase.PhaseName);
                Assert.Equal(2, phase.Order);
                Assert.Equal(33, phase.BossPercentage);
            });
    }

    [Fact]
    public void ParsePhaseProgress_returns_empty_for_invalid_json_syntax()
    {
        const string json = """{"data":{"graph":[{"phaseName":"P1","bestPull":"12%"}]""";

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        Assert.Empty(progress);
    }

    [Fact]
    public void ParsePhaseProgress_de_duplicates_phase_and_keeps_lower_boss_hp()
    {
        const string json = """
        [
          { "phaseName": "Phase Alpha", "order": 2, "bestPull": "42%" },
          { "phase_name": "Phase Alpha", "order": 2, "boss_percentage": "21%" },
          { "phase": { "name": "Phase Beta", "number": 1 }, "percent": 87 }
        ]
        """;

        var progress = TomestoneProgressParser.ParsePhaseProgress(json);

        Assert.Collection(
            progress,
            phase =>
            {
                Assert.Equal("phase-beta", phase.PhaseKey);
                Assert.Equal("Phase Beta", phase.PhaseName);
                Assert.Equal(1, phase.Order);
                Assert.Equal(87, phase.BossPercentage);
            },
            phase =>
            {
                Assert.Equal("phase-alpha", phase.PhaseKey);
                Assert.Equal("Phase Alpha", phase.PhaseName);
                Assert.Equal(2, phase.Order);
                Assert.Equal(21, phase.BossPercentage);
                Assert.Equal("Phase Alpha 21%", phase.DisplayText);
            });
    }

    [Fact]
    public void ParseProgress_extracts_savage_boss_percentage_without_phase_label()
    {
        const string json = """
        {
          "data": {
            "graph": [
              { "name": "Overall", "bestPull": "37.8%" },
              { "name": "Overall", "best_pull": "12.3%" }
            ]
          }
        }
        """;

        var progress = TomestoneProgressParser.ParseProgress(json);

        Assert.Empty(progress.Phases);
        Assert.Equal(12.3, progress.BossPercentage!.Value, 3);
    }

    [Fact]
    public void ParseProgress_extracts_plain_activity_best_percent_for_savage()
    {
        const string json = """
        {
          "activity": {
            "activities": {
              "activities": {
                "paginator": {
                  "data": [
                    { "activity": { "bestPercent": "48.20%", "killsCount": 0 } },
                    { "activity": { "bestPercent": "0.00%", "killsCount": 1 } },
                    { "activity": { "bestPercent": "16.75%", "killsCount": 0 } }
                  ]
                }
              }
            }
          }
        }
        """;

        var progress = TomestoneProgressParser.ParseProgress(json);

        Assert.Empty(progress.Phases);
        Assert.Equal(16.75, progress.BossPercentage!.Value, 3);
    }

    [Fact]
    public void ParseProgress_ignores_percentile_rank_style_fields()
    {
        var json = ReadFixture("progression-graph-malformed-shape.json");

        var progress = TomestoneProgressParser.ParseProgress(json);

        Assert.Empty(progress.Phases);
        Assert.Null(progress.BossPercentage);
    }

    [Fact]
    public void ParseProgress_for_target_prefers_target_raw_percent_over_unrelated_activity()
    {
        var json = ReadFixture("progression-target-m2s-raw-percent-with-unrelated-activity.json");

        var progress = TomestoneProgressParser.ParseProgress(json, M2SProgressTarget);

        Assert.False(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Equal(29.69, progress.BossPercentage!.Value, 3);
    }

    [Fact]
    public void ParseProgress_for_target_completed_activity_returns_cleared_without_progress()
    {
        var json = ReadFixture("progression-target-m2s-completed-with-wipe-activity.json");

        var progress = TomestoneProgressParser.ParseProgress(json, M2SProgressTarget);

        Assert.True(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Null(progress.BossPercentage);
    }

    [Fact]
    public void ParseProgress_for_target_direct_completed_at_returns_cleared_without_progress()
    {
        const string json = """
        {
          "data": {
            "encounters": [
              {
                "canonicalName": "red-hot-deep-blue",
                "completedAt": "2026-05-18T00:00:00Z",
                "progression": {
                  "rawPercent": 12.34
                }
              }
            ]
          }
        }
        """;

        var progress = TomestoneProgressParser.ParseProgress(json, M2SProgressTarget);

        Assert.True(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Null(progress.BossPercentage);
    }

    [Fact]
    public void ParseProgress_for_target_fallback_uses_only_matching_activity_rows()
    {
        var json = ReadFixture("progression-target-m2s-activity-fallback-with-unrelated-row.json");

        var progress = TomestoneProgressParser.ParseProgress(json, M2SProgressTarget);

        Assert.False(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Equal(46.35, progress.BossPercentage!.Value, 3);
    }

    [Fact]
    public void ParseProgress_for_boss_target_activity_fallback_ignores_endpoint_slug_when_explicit_target_exists()
    {
        const string json = """
        {
          "activity": {
            "activities": {
              "activities": {
                "paginator": {
                  "data": [
                    {
                      "activity": {
                        "bestPercent": "7.00%",
                        "killsCount": 0,
                        "encounter": {
                          "canonical_name": "aac-heavyweight-m2-savage"
                        }
                      }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        var progress = TomestoneProgressParser.ParseProgress(json, M2SProgressTarget);

        Assert.False(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Null(progress.BossPercentage);
    }

    [Fact]
    public void ParseProgress_for_target_fallback_uses_only_matching_sources_within_target_row()
    {
        const string json = """
        {
          "activity": {
            "activities": {
              "activities": {
                "paginator": {
                  "data": [
                    {
                      "activity": {
                        "bestPercent": "50.00%",
                        "killsCount": 0,
                        "encounter": {
                          "canonical_name": "red-hot-deep-blue"
                        }
                      },
                      "perspectives": {
                        "otherEncounter": {
                          "bestPercent": "5.00%",
                          "killsCount": 0,
                          "encounter": {
                            "canonical_name": "vamp-fatale"
                          }
                        },
                        "targetKill": {
                          "bestPercent": "0.00%",
                          "killsCount": 1,
                          "encounter": {
                            "canonical_name": "red-hot-deep-blue"
                          }
                        }
                      }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        var progress = TomestoneProgressParser.ParseProgress(json, M2SProgressTarget);

        Assert.False(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Equal(50.00, progress.BossPercentage!.Value, 3);
    }

    [Fact]
    public void ParseProgress_for_m4s_p1_only_lindwurm_does_not_store_final_boss_percentage()
    {
        const string json = """
        {
          "data": {
            "encounters": [
              {
                "canonicalName": "lindwurm",
                "progression": {
                  "rawPercent": 19.25
                }
              }
            ]
          }
        }
        """;

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(73, 105, out var target));

        var progress = TomestoneProgressParser.ParseProgress(json, target);

        Assert.False(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Null(progress.BossPercentage);
    }

    [Fact]
    public void ParseProgress_for_m4s_both_lindwurm_nodes_stores_lindwurm_ii_boss_percentage()
    {
        const string json = """
        {
          "data": {
            "encounters": [
              {
                "canonicalName": "lindwurm",
                "progression": {
                  "rawPercent": 19.25
                }
              },
              {
                "canonicalName": "lindwurm-ii",
                "progression": {
                  "rawPercent": 41.75
                }
              }
            ]
          }
        }
        """;

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(73, 105, out var target));

        var progress = TomestoneProgressParser.ParseProgress(json, target);

        Assert.False(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Equal(41.75, progress.BossPercentage!.Value, 3);
    }

    [Fact]
    public void ParseProgress_for_phase_target_uses_target_canonical_names()
    {
        const string json = """
        {
          "data": {
            "encounters": [
              {
                "canonicalName": "unrelated-phase",
                "progression": {
                  "displayPercent": "3.25% P1"
                },
                "mechanic": {
                  "number": 1
                }
              },
              {
                "canonicalName": "target-phase",
                "progression": {
                  "displayPercent": "42.5% P2"
                },
                "mechanic": {
                  "number": 2
                }
              }
            ]
          }
        }
        """;

        var progress = TomestoneProgressParser.ParseProgress(json, PhaseProgressTarget);

        Assert.False(progress.Cleared);
        Assert.Null(progress.BossPercentage);
        var phase = Assert.Single(progress.Phases);
        Assert.Equal("p2", phase.PhaseKey);
        Assert.Equal("P2", phase.PhaseName);
        Assert.Equal(2, phase.Order);
        Assert.Equal(42.5, phase.BossPercentage);
        Assert.Equal("P2 42.5%", phase.DisplayText);
    }

    [Fact]
    public void ParseProgress_keeps_nested_savage_encounters_targeted()
    {
        const string json = """
        {"encounters":{"savage":[{"name":"Heavyweight","encounters":[
          {"canonicalName":"red-hot-deep-blue","progression":{"displayPercent":"21.68%"}},
          {"canonicalName":"vamp-fatale","progression":{"displayPercent":"1.00%"}}
        ]}]}}
        """;

        var progress = TomestoneProgressParser.ParseProgress(json, M2SProgressTarget);

        Assert.Equal(21.68, progress.BossPercentage);
        Assert.Empty(progress.Phases);
    }

    [Fact]
    public void ParseProgress_does_not_treat_mechanic_occurrence_as_a_phase()
    {
        const string json = """
        {"encounters":{"ultimate":[{"canonicalName":"target-phase","progression":{
          "mechanic":{"name":"Splattershed","number":2},"displayPercent":"75.71%"
        }}]}}
        """;

        var progress = TomestoneProgressParser.ParseProgress(json, PhaseProgressTarget);

        var phase = Assert.Single(progress.Phases);
        Assert.Equal("mechanic-splattershed-2", phase.PhaseKey);
        Assert.Equal("Splattershed #2", phase.PhaseName);
        Assert.Equal(0, phase.Order);
        Assert.Equal(75.71, phase.BossPercentage);
        Assert.Null(progress.BossPercentage);
        Assert.Empty(TomestoneProgressParser.ParsePhaseProgress(
            """{"graph":[{"mechanic":{"name":"Splattershed","number":2},"percent":75.71}]}"""));
    }

    [Theory]
    [InlineData("{\"phase\":\"P5\",\"rawPercent\":75.71}")]
    [InlineData("{\"phase\":{\"name\":\"P5\"},\"displayPercent\":\"75.71%\"}")]
    [InlineData("{\"phaseName\":\"P5\",\"rawPercent\":75.71}")]
    [InlineData("{\"bestPercent\":\"75.71% P5\",\"mechanic\":{\"number\":2}}")]
    public void ParseProgress_reads_only_explicit_profile_phase(string progression)
    {
        var json = "{\"encounters\":{\"ultimate\":[{\"canonicalName\":\"target-phase\",\"progression\":" + progression + "}]}}";

        var phase = Assert.Single(TomestoneProgressParser.ParseProgress(json, PhaseProgressTarget).Phases);

        Assert.Equal("P5", phase.PhaseName);
        Assert.Equal(5, phase.Order);
        Assert.Equal(75.71, phase.BossPercentage);
    }

    [Theory]
    [InlineData("[{\"activity\":{\"bestPercent\":\"79.71% P5\"}}]")]
    [InlineData("{\"results\":[{\"activity\":{\"bestPercent\":\"79.71% P5\"}}]}")]
    [InlineData("{\"data\":[{\"bestPercent\":\"79.71% P5\"}]}")]
    [InlineData("{\"data\":{\"paginator\":{\"data\":[{\"bestPercent\":\"79.71% P5\"}]}}}")]
    public void ParseEncounterActivityProgress_supports_official_activity_response_shapes(string json)
    {
        var progress = TomestoneProgressParser.ParseEncounterActivityProgress(json, PhaseProgressTarget);

        var phase = Assert.Single(progress.Phases);
        Assert.Equal("P5", phase.PhaseName);
        Assert.Equal(79.71, phase.BossPercentage);
        Assert.Null(progress.BossPercentage);
    }

    [Theory]
    [InlineData("21.68")]
    [InlineData("\"21.68%\"")]
    public void ParseEncounterActivityProgress_reads_numeric_and_plain_boss_percentages(string percent)
    {
        var json = "{\"data\":[{\"activity\":{\"bestPercent\":" + percent + ",\"killsCount\":0}}]}";

        var progress = TomestoneProgressParser.ParseEncounterActivityProgress(json, M2SProgressTarget);

        Assert.Empty(progress.Phases);
        Assert.Equal(21.68, progress.BossPercentage);
    }

    [Fact]
    public void ParseEncounterActivityProgress_rejects_unrelated_and_kill_rows_and_sources()
    {
        const string json = """
        {"results":[
          {"activity":{"bestPercent":"1% P6","encounter":{"canonical_name":"unrelated"}}},
          {"activity":{"bestPercent":"0% P6","killsCount":1}},
          {"bestPercent":"0% P6","killsCount":1},
          {"activity":{"bestPercent":"75% P4","encounter":{"canonical_name":"target-phase"}},
           "perspectives":{
             "unrelated":{"bestPercent":"2% P6","encounter":{"canonical_name":"unrelated"}},
             "kill":{"bestPercent":"0% P6","killsCount":1}
           }},
          {"bestPercent":"79.71% P5"}
        ]}
        """;

        var phase = Assert.Single(TomestoneProgressParser.ParseEncounterActivityProgress(json, PhaseProgressTarget).Phases);

        Assert.Equal("P5", phase.PhaseName);
        Assert.Equal(79.71, phase.BossPercentage);
    }

    [Fact]
    public void ParseProgress_does_not_assign_unlabelled_or_unrelated_profile_activity_to_target()
    {
        const string json = """
        {"encounters":{"ultimate":[{"canonicalName":"unrelated","progression":{"displayPercent":"1% P6"}}]},
         "activity":{"activities":{"activities":{"paginator":{"data":[
          {"bestPercent":"1% P6"},
          {"activity":{"bestPercent":"1% P6","encounter":{"canonical_name":"unrelated"}}}
        ]}}}}}
        """;

        var progress = TomestoneProgressParser.ParseProgress(json, PhaseProgressTarget);

        Assert.Empty(progress.Phases);
        Assert.Null(progress.BossPercentage);
    }

    [Fact]
    public void ParseProgress_accepts_only_matching_profile_activity_sources()
    {
        const string json = """
        {"activity":{"activities":{"activities":{"paginator":{"data":[
          {"activity":{"bestPercent":"79.71% P5","encounter":{"canonical_name":"target-phase"}},
           "perspectives":{"other":{"bestPercent":"1% P6","encounter":{"canonical_name":"unrelated"}}}}
        ]}}}}}
        """;

        var phase = Assert.Single(TomestoneProgressParser.ParseProgress(json, PhaseProgressTarget).Phases);

        Assert.Equal("P5", phase.PhaseName);
        Assert.Equal(79.71, phase.BossPercentage);
    }

    [Theory]
    [InlineData("activity", "{\"completionWeek\":\"Week 3\"}")]
    [InlineData("achievement", "{\"completionWeek\":\"Week 7\"}")]
    [InlineData("achievement", "{\"completionWeek\":7}")]
    [InlineData("achievement", "{\"completedAt\":\"2026-10-17T00:00:00Z\"}")]
    public void ParseProgress_marks_profile_activity_or_public_achievement_as_cleared(string field, string clear)
    {
        var json = "{\"encounters\":{\"ultimate\":[{\"canonicalName\":\"target-phase\",\"" + field + "\":" + clear
            + ",\"progression\":{\"displayPercent\":\"75.71% P5\"}}]}}";

        var progress = TomestoneProgressParser.ParseProgress(json, PhaseProgressTarget);

        Assert.True(progress.Cleared);
        Assert.Empty(progress.Phases);
        Assert.Null(progress.BossPercentage);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"completionWeek\":null}")]
    [InlineData("{\"completionWeek\":\" \"}")]
    [InlineData("{\"completionWeek\":0}")]
    [InlineData("{\"completionWeek\":\"0\"}")]
    [InlineData("{\"completedAt\":null}")]
    public void ParseProgress_does_not_mark_empty_completion_metadata_as_cleared(string clear)
    {
        var json = "{\"encounters\":{\"ultimate\":[{\"canonicalName\":\"target-phase\",\"activity\":" + clear
            + ",\"achievement\":" + clear + ",\"progression\":{\"displayPercent\":\"75.71% P5\"}}]}}";

        var progress = TomestoneProgressParser.ParseProgress(json, PhaseProgressTarget);

        Assert.False(progress.Cleared);
        Assert.Single(progress.Phases);
    }

    [Fact]
    public void ParseProgress_does_not_use_unrelated_encounter_completion_metadata()
    {
        const string json = """
        {"encounters":{"ultimate":[
          {"canonicalName":"unrelated","achievement":{"completionWeek":"Week 3"}},
          {"canonicalName":"target-phase","progression":{"displayPercent":"75.71% P5"}}
        ]}}
        """;

        var progress = TomestoneProgressParser.ParseProgress(json, PhaseProgressTarget);

        Assert.False(progress.Cleared);
        Assert.Single(progress.Phases);
    }

    private static string ReadFixture(
        string fileName,
        [CallerFilePath] string sourceFile = "")
    {
        var testDirectory = Path.GetDirectoryName(sourceFile)
            ?? throw new InvalidOperationException("Could not determine test source directory.");
        return File.ReadAllText(Path.Combine(testDirectory, "Fixtures", "tomestone", fileName));
    }
}
