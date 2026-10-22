# ScreenDriver Framework Reference

Use this reference only after confirming the consuming application uses
ScreenDriver and after inspecting its installed framework version.

## Investigation order

1. Locate the flow definition and module registration.
2. Identify the generated definition endpoint or startup registration.
3. Trace the relevant control path through events, actions, task groups, and
   server state.
4. Locate client-side action, control, dialog, or container extensions.
5. Reproduce the interaction with representative non-production data.

## Common failure modes

- A visibility or validation expression references a value that is not tracked
  as a registered control.
- A server/client extension uses inconsistent action or control identifiers.
- A task group omits state required by a later event or validation pass.
- A custom client extension is not registered in the application's bootstrap.
- A dialog or navigation transition loses state because the consuming
  application's persistence strategy was not considered.

## Testing

Test flow-definition output, server task behaviour, validation/error mapping,
and the rendered interaction at the level supported by the consuming
application. Prefer public framework APIs and the app's established test tools.

## Reference flow pattern

Builder method names and signatures vary by installed ScreenDriver version, so
verify the local API before adapting this representative flow:

```csharp
public static FlowDefinition Build() =>
    new FlowBuilder<FormData>("sample")
        .WithFormStep("details")
            .WithSection("main", "Details")
                .HiddenControl(x => x.ShowExtra)
                .WithInput(x => x.Name).EndControl()
                .WithInput(x => x.Extra)
                    .IsVisibleWhen(x => x.ShowExtra)
                .EndControl()
                .WithButton("save", "Save")
                    .WithOnClickEvent()
                        .ExecuteTaskGroup(TaskNames.Save, showIsExecuting: true)
                        .CustomAction(ClientActions.Notify)
                            .Parameter("message", "Saved")
                        .EndAction()
                    .EndEvent()
                .EndControl()
            .EndSection()
        .EndStep()
        .WithTaskGroups()
            .WithTaskGroup<RequestContext>(TaskNames.Save)
                .Task<SaveTask>()
            .EndTaskGroup()
        .EndTaskGroups()
    .Build();
```

Every expression must reference a registered control in the same scope. Keep
the server-emitted action string synchronized with the client registry:

```typescript
export const clientActions = {
  notify: 'sample-notify',
} as const;

export class NotifyHandler implements IActionHandler {
  execute(parameters: { message: string }): boolean {
    // Perform client-only UI behaviour.
    return true;
  }
}

registry.register(clientActions.notify, NotifyHandler);
```

Custom controls follow the same wire-contract rule: use a stable control type
string in the server definition and register that exact string with the client
control registry. Test the emitted definition, isolated task-group execution,
and the registry-backed user interaction.
