using System;
using System.Collections.Generic;
using System.Text;

namespace SmartX.Shared;

/// <summary>
/// Represents one node in a multi-tier deployment tree, e.g.
/// Facility A -> Zone 1 -> Sub-Zone B -> Sensor.
/// </summary>
public class DeploymentNode
{
    public string Name { get; set; } = string.Empty;
    public bool IsSensorNode { get; set; }
    public List<DeploymentNode> Children { get; set; } = new();

    /// <summary>
    /// Recursively validates that a dotted path like
    /// "Facility A/Zone 1/Sub-Zone B" exists under this node.
    /// </summary>
    public bool ValidatePath(IReadOnlyList<string> path, int index = 0)
    {
        // Base case: name mismatch at this level
        if (index >= path.Count || !string.Equals(Name, path[index], StringComparison.OrdinalIgnoreCase))
            return false;

        // Base case: reached the end of the path successfully
        if (index == path.Count - 1)
            return true;

        // Recursive case: check each child for the next path segment
        foreach (var child in Children)
        {
            if (child.ValidatePath(path, index + 1))
                return true;
        }
        return false;
    }

    public static DeploymentNode BuildDemoTree() => new()
    {
        Name = "Facility A",
        Children =
        {
            new DeploymentNode
            {
                Name = "Zone 1",
                Children =
                {
                    new DeploymentNode { Name = "Sub-Zone B", IsSensorNode = true },
                    new DeploymentNode { Name = "Sub-Zone C", IsSensorNode = true }
                }
            },
            new DeploymentNode
            {
                Name = "Zone 2",
                Children = { new DeploymentNode { Name = "Sub-Zone D", IsSensorNode = true } }
            }
        }
    };
}